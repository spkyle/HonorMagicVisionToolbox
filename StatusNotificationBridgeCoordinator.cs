using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TopBarAdaptiveColor
{
    internal sealed class BridgedNotificationSnapshot
    {
        [JsonPropertyName("generation")]
        public long Generation { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("topBarHwnd")]
        public long TopBarHwnd { get; set; }

        [JsonPropertyName("items")]
        public List<BridgedNotificationRecord> Items { get; set; } = new();
    }

    internal sealed class BridgedNotificationRecord
    {
        [JsonPropertyName("guid")]
        public string Guid { get; set; } = string.Empty;

        [JsonPropertyName("appKey")]
        public string AppKey { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("iconPath")]
        public string IconPath { get; set; } = string.Empty;

        [JsonPropertyName("targetHwnd")]
        public long TargetHwnd { get; set; }

        [JsonPropertyName("launchPath")]
        public string LaunchPath { get; set; } = string.Empty;

        [JsonPropertyName("processName")]
        public string ProcessName { get; set; } = string.Empty;

        [JsonPropertyName("timestampMs")]
        public long TimestampMs { get; set; }

        [JsonPropertyName("episodeId")]
        public long EpisodeId { get; set; }
    }

    internal sealed class BridgedNotificationAckSnapshot
    {
        [JsonPropertyName("generation")]
        public long Generation { get; set; }

        [JsonPropertyName("dismissed")]
        public List<BridgedNotificationAckEntry> Dismissed { get; set; } = new();
    }

    internal sealed class BridgedNotificationAckEntry
    {
        [JsonPropertyName("guid")]
        public string Guid { get; set; } = string.Empty;

        [JsonPropertyName("episodeId")]
        public long EpisodeId { get; set; }

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("handled")]
        public bool Handled { get; set; }
    }

    /// <summary>
    /// Tracks unread StatusNotification items (WeChat, Feishu, DingTalk, QQ, etc.) on the TopBar,
    /// publishes active unread cards to %TEMP%\MagicTaskbar\bridged-notifications.json for
    /// MessageCenterBridge.dll, and synchronizes click/dismiss/read state back to the top-right bell.
    /// </summary>
    internal sealed class StatusNotificationBridgeCoordinator : IDisposable
    {
        internal const string BridgedGuidPrefix = "mvt-app-";
        internal const string SyncWindowMessageName = "MagicVisionToolbox_SyncNotifications";
        private const string MessageCenterWindowTitle = "HONOR Message Center";
        private const uint ShgfiIcon = 0x000000100;
        private const uint ShgfiLargeIcon = 0x000000000;

        private sealed class EpisodeState
        {
            public long EpisodeId { get; }
            public long TimestampMs { get; }
            public bool BellAcked { get; set; }
            public bool CardDismissed { get; set; }

            public EpisodeState(long episodeId, long timestampMs)
            {
                EpisodeId = episodeId;
                TimestampMs = timestampMs;
            }
        }

        private sealed class ItemAccessor
        {
            public PropertyInfo? HasUnreadProp { get; }
            public PropertyInfo? AppIdProp { get; }
            public PropertyInfo? DisplayNameProp { get; }
            public PropertyInfo? LaunchPathProp { get; }
            public PropertyInfo? IconUriProp { get; }
            public PropertyInfo? BitmapIconUriProp { get; }
            public PropertyInfo? UnreadWindowHandleProp { get; }

            public ItemAccessor(Type t)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                HasUnreadProp = t.GetProperty("HasUnread", flags);
                AppIdProp = t.GetProperty("AppId", flags);
                DisplayNameProp = t.GetProperty("DisplayName", flags);
                LaunchPathProp = t.GetProperty("LaunchPath", flags);
                IconUriProp = t.GetProperty("IconUri", flags);
                BitmapIconUriProp = t.GetProperty("BitmapIconUri", flags);
                UnreadWindowHandleProp = t.GetProperty("UnreadWindowHandle", flags);
            }
        }

        internal static readonly string DefaultTempDir = Path.Combine(Path.GetTempPath(), "MagicTaskbar");
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
        private static readonly object StaticSync = new();
        private static long _lastIssuedEpisodeId;

        private readonly Window _topBarWindow;
        private readonly ItemsControl? _itemsControl;
        private readonly Action _onAlertStateChanged;
        private readonly Action<BridgedNotificationRecord>? _onActivateBridgedRecord;
        private readonly string _tempDir;
        private readonly string _statePath;
        private readonly string _ackPath;
        private readonly string _iconsDir;
        private readonly Dictionary<string, EpisodeState> _episodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _activatedEpisodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BridgedNotificationRecord> _lastRecordsByGuid = new(StringComparer.Ordinal);
        private readonly Dictionary<object, INotifyPropertyChanged> _hookedItems = new();
        private readonly Dictionary<Type, ItemAccessor> _accessors = new();
        private readonly uint _syncMsgId;

        private INotifyCollectionChanged? _hookedCollection;
        private HwndSource? _hwndSource;
        private HwndSourceHook? _hwndHook;
        private string? _lastPublishedPayloadKey;
        private Dictionary<string, BridgedNotificationAckEntry> _cachedAckEntries = new(StringComparer.Ordinal);
        private DateTime _cachedAckWriteUtc = DateTime.MinValue;
        private long _cachedAckLength = -1;
        private long _generation;
        private bool _disposed;

        public bool HasUnackedBellAlert { get; private set; }
        public bool HasAnyActiveCard { get; private set; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessageW(string lpString);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, out SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFOW
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        public StatusNotificationBridgeCoordinator(
            Window topBarWindow,
            ItemsControl? itemsControl,
            Action onAlertStateChanged,
            string? customTempDir = null,
            Action<BridgedNotificationRecord>? onActivateBridgedRecord = null)
        {
            _topBarWindow = topBarWindow ?? throw new ArgumentNullException(nameof(topBarWindow));
            _itemsControl = itemsControl;
            _onAlertStateChanged = onAlertStateChanged ?? throw new ArgumentNullException(nameof(onAlertStateChanged));
            _onActivateBridgedRecord = onActivateBridgedRecord;
            _tempDir = string.IsNullOrWhiteSpace(customTempDir) ? DefaultTempDir : customTempDir;
            _statePath = Path.Combine(_tempDir, "bridged-notifications.json");
            _ackPath = Path.Combine(_tempDir, "bridged-notifications-ack.json");
            _iconsDir = Path.Combine(_tempDir, "bridged-icons");
            _syncMsgId = RegisterWindowMessageW(SyncWindowMessageName);

            HookItemsCollection();
            HookTopBarHwnd();
            Sync(notifyOnChange: false);
        }

        public void AcknowledgeBellForActiveEpisodes()
        {
            if (_disposed)
            {
                return;
            }

            bool changed = false;
            foreach (EpisodeState ep in _episodes.Values)
            {
                if (!ep.BellAcked)
                {
                    ep.BellAcked = true;
                    changed = true;
                }
            }

            if (changed)
            {
                Sync(notifyOnChange: false);
            }
        }

        public bool Sync(bool notifyOnChange = true) => SyncCore(notifyOnChange, forceReadAck: false);

        private bool SyncCore(bool notifyOnChange, bool forceReadAck)
        {
            if (_disposed)
            {
                return false;
            }

            RefreshItemSubscriptions();

            bool enabled = AdaptiveSettingsStore.IsNotificationAlertAnimationEnabled;
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Dictionary<string, BridgedNotificationAckEntry> ackEntries = ReadAckEntriesCached(forceReadAck);
            var seenGuids = new HashSet<string>(StringComparer.Ordinal);
            var activeRecords = new List<BridgedNotificationRecord>();

            if (_itemsControl != null)
            {
                foreach (object? item in _itemsControl.Items)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    ItemAccessor acc = GetAccessor(item.GetType());
                    if (acc.HasUnreadProp?.GetValue(item) is not true)
                    {
                        continue;
                    }

                    string? appId = acc.AppIdProp?.GetValue(item) as string;
                    string? displayName = acc.DisplayNameProp?.GetValue(item) as string;
                    string? launchPath = acc.LaunchPathProp?.GetValue(item) as string;
                    IntPtr targetHwnd = acc.UnreadWindowHandleProp?.GetValue(item) is IntPtr hwnd ? hwnd : IntPtr.Zero;

                    string appKey = BuildAppKey(appId, displayName, launchPath);
                    string guid = BridgedGuidPrefix + appKey;
                    if (!seenGuids.Add(guid))
                    {
                        continue;
                    }

                    if (!_episodes.TryGetValue(guid, out EpisodeState? ep))
                    {
                        ep = new EpisodeState(NextEpisodeId(nowMs), nowMs);
                        _episodes[guid] = ep;
                    }

                    long targetHwndValue = targetHwnd.ToInt64();
                    if (!_lastRecordsByGuid.TryGetValue(guid, out BridgedNotificationRecord? record) ||
                        record.EpisodeId != ep.EpisodeId ||
                        record.TargetHwnd != targetHwndValue)
                    {
                        Uri? bitmapUri = acc.BitmapIconUriProp?.GetValue(item) as Uri;
                        Uri? iconUri = acc.IconUriProp?.GetValue(item) as Uri;
                        string name = !string.IsNullOrWhiteSpace(displayName)
                            ? displayName.Trim()
                            : (!string.IsNullOrWhiteSpace(appId) ? appId.Trim() : appKey);

                        string processName = !string.IsNullOrWhiteSpace(launchPath)
                            ? Path.GetFileNameWithoutExtension(launchPath)
                            : string.Empty;

                        string iconPath = ResolveCachedIconPngPath(appKey, bitmapUri, iconUri, launchPath);
                        record = new BridgedNotificationRecord
                        {
                            Guid = guid,
                            AppKey = appKey,
                            DisplayName = name,
                            Title = name + " 有新消息",
                            Description = "点击跳转查看未读消息",
                            IconPath = iconPath,
                            TargetHwnd = targetHwndValue,
                            LaunchPath = launchPath ?? string.Empty,
                            ProcessName = processName,
                            TimestampMs = ep.TimestampMs,
                            EpisodeId = ep.EpisodeId
                        };
                        _lastRecordsByGuid[guid] = record;
                    }

                    if (ackEntries.TryGetValue(guid, out BridgedNotificationAckEntry? ackEntry) &&
                        ackEntry.EpisodeId == ep.EpisodeId)
                    {
                        ep.CardDismissed = true;
                        ep.BellAcked = true;
                        TryDispatchAckActivation(guid, ackEntry, record);
                    }

                    if (!enabled || ep.CardDismissed)
                    {
                        continue;
                    }

                    activeRecords.Add(record);
                }
            }

            if (_episodes.Count > 0)
            {
                List<string>? clearedGuids = null;
                foreach (string existingGuid in _episodes.Keys)
                {
                    if (!seenGuids.Contains(existingGuid))
                    {
                        if (ackEntries.TryGetValue(existingGuid, out BridgedNotificationAckEntry? ackEntry) &&
                            _lastRecordsByGuid.TryGetValue(existingGuid, out BridgedNotificationRecord? cachedRecord) &&
                            ackEntry.EpisodeId == cachedRecord.EpisodeId)
                        {
                            TryDispatchAckActivation(existingGuid, ackEntry, cachedRecord);
                        }

                        clearedGuids ??= new List<string>();
                        clearedGuids.Add(existingGuid);
                    }
                }

                if (clearedGuids != null)
                {
                    foreach (string g in clearedGuids)
                    {
                        _episodes.Remove(g);
                        _activatedEpisodes.Remove(g);
                        _lastRecordsByGuid.Remove(g);
                    }
                }
            }

            bool prevBell = HasUnackedBellAlert;
            bool nextBell = false;
            if (enabled)
            {
                foreach (EpisodeState ep in _episodes.Values)
                {
                    if (!ep.CardDismissed && !ep.BellAcked)
                    {
                        nextBell = true;
                        break;
                    }
                }
            }

            HasUnackedBellAlert = nextBell;
            HasAnyActiveCard = activeRecords.Count > 0;

            PublishSnapshotIfChanged(enabled, activeRecords);

            if (notifyOnChange && prevBell != nextBell)
            {
                _onAlertStateChanged();
            }

            return prevBell != nextBell;
        }

        private bool HasAnyUnreadStatusItem()
        {
            if (_itemsControl == null)
            {
                return false;
            }

            foreach (object? item in _itemsControl.Items)
            {
                if (item != null && GetAccessor(item.GetType()).HasUnreadProp?.GetValue(item) is true)
                {
                    return true;
                }
            }

            return false;
        }

        private Dictionary<string, BridgedNotificationAckEntry> ReadAckEntriesCached(bool forceReadAck)
        {
            if (!forceReadAck && _episodes.Count == 0 && _lastRecordsByGuid.Count == 0 && !HasAnyUnreadStatusItem())
            {
                return _cachedAckEntries.Count == 0
                    ? _cachedAckEntries
                    : (_cachedAckEntries = new Dictionary<string, BridgedNotificationAckEntry>(StringComparer.Ordinal));
            }

            try
            {
                var info = new FileInfo(_ackPath);
                if (!info.Exists)
                {
                    if (_cachedAckEntries.Count > 0 || _cachedAckWriteUtc != DateTime.MinValue || _cachedAckLength != -1)
                    {
                        _cachedAckWriteUtc = DateTime.MinValue;
                        _cachedAckLength = -1;
                        _cachedAckEntries = new Dictionary<string, BridgedNotificationAckEntry>(StringComparer.Ordinal);
                    }
                    return _cachedAckEntries;
                }

                DateTime mtime = info.LastWriteTimeUtc;
                long length = info.Length;
                if (!forceReadAck && mtime == _cachedAckWriteUtc && length == _cachedAckLength)
                {
                    return _cachedAckEntries;
                }

                Dictionary<string, BridgedNotificationAckEntry> entries = ReadAckEntries(_ackPath);
                _cachedAckWriteUtc = mtime;
                _cachedAckLength = length;
                _cachedAckEntries = entries;
                return entries;
            }
            catch
            {
                return ReadAckEntries(_ackPath);
            }
        }

        private void TryDispatchAckActivation(
            string guid,
            BridgedNotificationAckEntry ackEntry,
            BridgedNotificationRecord record)
        {
            if (_onActivateBridgedRecord == null ||
                ackEntry.Handled ||
                !string.Equals(ackEntry.Action, "activate", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_activatedEpisodes.TryGetValue(guid, out long activatedEp) && activatedEp == ackEntry.EpisodeId)
            {
                return;
            }

            _activatedEpisodes[guid] = ackEntry.EpisodeId;
            _onActivateBridgedRecord(record);
        }

        private void PublishSnapshotIfChanged(bool enabled, List<BridgedNotificationRecord> items)
        {
            long topBarHwnd = new WindowInteropHelper(_topBarWindow).Handle.ToInt64();
            var sb = new StringBuilder();
            sb.Append(enabled ? '1' : '0').Append('|').Append(topBarHwnd);
            foreach (BridgedNotificationRecord item in items)
            {
                sb.Append(';').Append(item.Guid).Append(':').Append(item.EpisodeId).Append(':').Append(item.TargetHwnd);
            }

            string payloadKey = sb.ToString();
            if (string.Equals(payloadKey, _lastPublishedPayloadKey, StringComparison.Ordinal))
            {
                return;
            }

            _lastPublishedPayloadKey = payloadKey;
            var snapshot = new BridgedNotificationSnapshot
            {
                Generation = ++_generation,
                Enabled = enabled,
                TopBarHwnd = topBarHwnd,
                Items = items
            };

            WriteJsonAtomic(_statePath, snapshot);
            PingMessageCenterWindows();
        }

        private void PingMessageCenterWindows()
        {
            if (_syncMsgId == 0)
            {
                return;
            }

            IntPtr hAfter = IntPtr.Zero;
            while (true)
            {
                IntPtr hWnd = FindWindowExW(IntPtr.Zero, hAfter, null, MessageCenterWindowTitle);
                if (hWnd == IntPtr.Zero)
                {
                    break;
                }

                hAfter = hWnd;
                PostMessageW(hWnd, _syncMsgId, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private void HookTopBarHwnd()
        {
            IntPtr hwnd = new WindowInteropHelper(_topBarWindow).Handle;
            if (hwnd == IntPtr.Zero)
            {
                _topBarWindow.SourceInitialized += OnTopBarSourceInitialized;
                return;
            }

            AttachHwndSource(hwnd);
        }

        private void OnTopBarSourceInitialized(object? sender, EventArgs e)
        {
            _topBarWindow.SourceInitialized -= OnTopBarSourceInitialized;
            if (!_disposed)
            {
                AttachHwndSource(new WindowInteropHelper(_topBarWindow).Handle);
                Sync(notifyOnChange: true);
            }
        }

        private void AttachHwndSource(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || _hwndSource != null)
            {
                return;
            }

            _hwndSource = HwndSource.FromHwnd(hwnd);
            if (_hwndSource != null)
            {
                _hwndHook = TopBarWndProc;
                _hwndSource.AddHook(_hwndHook);
            }
        }

        private IntPtr TopBarWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (!_disposed && _syncMsgId != 0 && (uint)msg == _syncMsgId)
            {
                _topBarWindow.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    if (!_disposed)
                    {
                        SyncCore(notifyOnChange: true, forceReadAck: true);
                    }
                }));
            }

            return IntPtr.Zero;
        }

        private void HookItemsCollection()
        {
            if (_itemsControl == null)
            {
                return;
            }

            if (_itemsControl.Items is INotifyCollectionChanged ncc)
            {
                _hookedCollection = ncc;
                ncc.CollectionChanged += OnStatusItemsCollectionChanged;
            }

            RefreshItemSubscriptions();
        }

        private void OnStatusItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (!_disposed)
            {
                ScheduleSync();
            }
        }

        private void RefreshItemSubscriptions()
        {
            if (_itemsControl == null)
            {
                return;
            }

            var current = new HashSet<object>();
            foreach (object? item in _itemsControl.Items)
            {
                if (item == null)
                {
                    continue;
                }

                current.Add(item);
                if (!_hookedItems.ContainsKey(item) && item is INotifyPropertyChanged npc)
                {
                    npc.PropertyChanged += OnStatusItemPropertyChanged;
                    _hookedItems[item] = npc;
                }
            }

            if (_hookedItems.Count > current.Count)
            {
                var removed = new List<object>();
                foreach (KeyValuePair<object, INotifyPropertyChanged> kv in _hookedItems)
                {
                    if (!current.Contains(kv.Key))
                    {
                        kv.Value.PropertyChanged -= OnStatusItemPropertyChanged;
                        removed.Add(kv.Key);
                    }
                }

                foreach (object r in removed)
                {
                    _hookedItems.Remove(r);
                }
            }
        }

        private void OnStatusItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            if (string.IsNullOrEmpty(e.PropertyName) ||
                string.Equals(e.PropertyName, "HasUnread", StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, "UnreadWindowHandle", StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, "DisplayName", StringComparison.Ordinal))
            {
                ScheduleSync();
            }
        }

        private void ScheduleSync()
        {
            if (_topBarWindow.Dispatcher.CheckAccess())
            {
                Sync(notifyOnChange: true);
            }
            else
            {
                _topBarWindow.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => Sync(notifyOnChange: true)));
            }
        }

        private ItemAccessor GetAccessor(Type t)
        {
            if (!_accessors.TryGetValue(t, out ItemAccessor? acc))
            {
                acc = new ItemAccessor(t);
                _accessors[t] = acc;
            }
            return acc;
        }

        private string ResolveCachedIconPngPath(string appKey, Uri? bitmapUri, Uri? iconUri, string? launchPath)
        {
            try
            {
                Directory.CreateDirectory(_iconsDir);
                string dstPng = Path.Combine(_iconsDir, appKey + ".png");
                if (File.Exists(dstPng) && new FileInfo(dstPng).Length > 0)
                {
                    return dstPng;
                }

                if (TrySaveUriToPng(bitmapUri, dstPng) || TrySaveUriToPng(iconUri, dstPng))
                {
                    return dstPng;
                }

                if (!string.IsNullOrWhiteSpace(launchPath) && File.Exists(launchPath) && TrySaveExeIconToPng(launchPath, dstPng))
                {
                    return dstPng;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"ResolveCachedIconPngPath failed for {appKey}: {ex.Message}");
            }

            return string.Empty;
        }

        private static bool TrySaveUriToPng(Uri? uri, string dstPng)
        {
            if (uri == null)
            {
                return false;
            }

            try
            {
                if (uri.IsFile)
                {
                    string local = uri.LocalPath;
                    if (!File.Exists(local))
                    {
                        return false;
                    }

                    string ext = Path.GetExtension(local);
                    if (string.Equals(ext, ".svg", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    if (string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(local, dstPng, overwrite: true);
                        return true;
                    }

                    using FileStream fs = File.OpenRead(local);
                    return EncodeStreamToPng(fs, dstPng);
                }

                if (string.Equals(uri.Scheme, "pack", StringComparison.OrdinalIgnoreCase))
                {
                    if (uri.AbsoluteUri.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    var res = Application.GetResourceStream(uri);
                    if (res?.Stream != null)
                    {
                        using Stream s = res.Stream;
                        return EncodeStreamToPng(s, dstPng);
                    }
                }
            }
            catch
            {
                // Ignore unsupported URI formats and fall back to exe icon extraction.
            }

            return false;
        }

        private static bool EncodeStreamToPng(Stream stream, string dstPng)
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return false;
            }

            BitmapFrame best = decoder.Frames[0];
            for (int i = 1; i < decoder.Frames.Count; i++)
            {
                if (decoder.Frames[i].PixelWidth > best.PixelWidth)
                {
                    best = decoder.Frames[i];
                }
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(best);
            using FileStream outFs = File.Create(dstPng);
            encoder.Save(outFs);
            return File.Exists(dstPng) && new FileInfo(dstPng).Length > 0;
        }

        private static bool TrySaveExeIconToPng(string exePath, string dstPng)
        {
            IntPtr res = SHGetFileInfoW(exePath, 0, out SHFILEINFOW info, (uint)Marshal.SizeOf<SHFILEINFOW>(), ShgfiIcon | ShgfiLargeIcon);
            if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                BitmapSource src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(src));
                using FileStream outFs = File.Create(dstPng);
                encoder.Save(outFs);
                return File.Exists(dstPng) && new FileInfo(dstPng).Length > 0;
            }
            finally
            {
                _ = DestroyIcon(info.hIcon);
            }
        }

        internal static string BuildAppKey(string? appId, string? displayName, string? launchPath)
        {
            string raw = !string.IsNullOrWhiteSpace(appId)
                ? appId
                : (!string.IsNullOrWhiteSpace(launchPath) ? Path.GetFileNameWithoutExtension(launchPath) : displayName ?? "app");

            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw.ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_')
                {
                    sb.Append(c);
                }
            }

            if (sb.Length > 0)
            {
                return sb.ToString();
            }

            // Deterministic ASCII key when display name has only CJK characters.
            unchecked
            {
                uint hash = 2166136261u;
                foreach (char c in raw)
                {
                    hash = (hash ^ c) * 16777619u;
                }
                return "app-" + hash.ToString("x8");
            }
        }

        private static long NextEpisodeId(long nowMs)
        {
            lock (StaticSync)
            {
                long next = Math.Max(nowMs, _lastIssuedEpisodeId + 1);
                _lastIssuedEpisodeId = next;
                return next;
            }
        }

        internal static Dictionary<string, long> ReadAckMap(string ackPath)
        {
            Dictionary<string, BridgedNotificationAckEntry> entries = ReadAckEntries(ackPath);
            var result = new Dictionary<string, long>(entries.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, BridgedNotificationAckEntry> kv in entries)
            {
                result[kv.Key] = kv.Value.EpisodeId;
            }
            return result;
        }

        internal static Dictionary<string, BridgedNotificationAckEntry> ReadAckEntries(string ackPath)
        {
            var result = new Dictionary<string, BridgedNotificationAckEntry>(StringComparer.Ordinal);
            if (!File.Exists(ackPath))
            {
                return result;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    string json = File.ReadAllText(ackPath, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return result;
                    }

                    var snapshot = JsonSerializer.Deserialize<BridgedNotificationAckSnapshot>(json, JsonOptions);
                    if (snapshot?.Dismissed != null)
                    {
                        foreach (BridgedNotificationAckEntry entry in snapshot.Dismissed)
                        {
                            if (!string.IsNullOrEmpty(entry.Guid) && entry.EpisodeId > 0)
                            {
                                result[entry.Guid] = entry;
                            }
                        }
                    }

                    return result;
                }
                catch
                {
                    Thread.Sleep(15);
                }
            }

            return result;
        }

        internal static void WriteJsonAtomic<T>(string path, T value)
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(value, JsonOptions);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                File.Copy(tmp, path, overwrite: true);
                try { File.Delete(tmp); } catch { }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"WriteJsonAtomic({Path.GetFileName(path)}) failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _topBarWindow.SourceInitialized -= OnTopBarSourceInitialized;

            if (_hwndSource != null && _hwndHook != null)
            {
                _hwndSource.RemoveHook(_hwndHook);
                _hwndSource = null;
                _hwndHook = null;
            }

            if (_hookedCollection != null)
            {
                _hookedCollection.CollectionChanged -= OnStatusItemsCollectionChanged;
                _hookedCollection = null;
            }

            foreach (KeyValuePair<object, INotifyPropertyChanged> kv in _hookedItems)
            {
                kv.Value.PropertyChanged -= OnStatusItemPropertyChanged;
            }
            _hookedItems.Clear();

            // Clear bridged cards cleanly on disposal when TopBar exits/reloads.
            PublishSnapshotIfChanged(enabled: false, new List<BridgedNotificationRecord>());
        }
    }
}

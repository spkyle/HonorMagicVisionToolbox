using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

[assembly: AssemblyTitle("MessageCenterBridge")]
[assembly: AssemblyProduct("MessageCenterBridge")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace MessageCenterBridge
{
    /// <summary>
    /// Loaded into HONOR MessageCenterUI.exe (.NET Framework 4.8) via MessageCenterUI.exe.config
    /// AppDomainManager. Mirrors unread status-bar app notifications (e.g. WeChat, Feishu, DingTalk, QQ)
    /// written by TopBarAdaptiveColor into native MessageCenterUI cards, and syncs clicks/dismissals back.
    /// </summary>
    public sealed class MessageCenterDomainManager : AppDomainManager
    {
        private static int _initialized;
        private static Timer _earlyAttachTimer;
        private static int _earlyAttachAttempts;

        public override void InitializeNewDomain(AppDomainSetup appDomainInfo)
        {
            base.InitializeNewDomain(appDomainInfo);

            if (Interlocked.Exchange(ref _initialized, 1) != 0)
            {
                return;
            }

            try
            {
                EventManager.RegisterClassHandler(
                    typeof(FrameworkElement),
                    FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnAnyElementLoaded),
                    true);

                _earlyAttachTimer = new Timer(TryEarlyAttachCallback, null, 150, 120);
                BridgeLog.Info("MessageCenterDomainManager initialized.");
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("InitializeNewDomain failed: " + ex);
            }
        }

        private static void TryEarlyAttachCallback(object state)
        {
            try
            {
                if (Interlocked.Increment(ref _earlyAttachAttempts) > 120)
                {
                    StopEarlyTimer();
                    return;
                }

                Application app = Application.Current;
                if (app == null || app.Dispatcher == null || app.Dispatcher.HasShutdownStarted)
                {
                    return;
                }

                app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate
                {
                    try
                    {
                        Application cur = Application.Current;
                        if (cur == null)
                        {
                            return;
                        }

                        bool attached = false;
                        for (int i = 0; i < cur.Windows.Count; i++)
                        {
                            Window w = cur.Windows[i];
                            if (w != null && MessageCenterBridgeController.IsHostWindowType(w))
                            {
                                MessageCenterBridgeController.AttachWindow(w);
                                attached = true;
                            }
                        }

                        if (attached)
                        {
                            StopEarlyTimer();
                        }
                    }
                    catch
                    {
                        // Retry until MessageCenter main window finishes construction.
                    }
                }));
            }
            catch
            {
                // Ignore transient startup state.
            }
        }

        private static void StopEarlyTimer()
        {
            Timer t = Interlocked.Exchange(ref _earlyAttachTimer, null);
            if (t != null)
            {
                t.Dispose();
            }
        }

        private static void OnAnyElementLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                Window w = sender as Window;
                if (w != null && MessageCenterBridgeController.IsHostWindowType(w))
                {
                    MessageCenterBridgeController.AttachWindow(w);
                    StopEarlyTimer();
                    return;
                }

                FrameworkElement fe = sender as FrameworkElement;
                if (fe != null)
                {
                    MessageCenterBridgeController.OnElementLoaded(fe);
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("OnAnyElementLoaded failed: " + ex.Message);
            }
        }
    }

    [DataContract]
    internal sealed class BridgedNotificationSnapshot
    {
        [DataMember(Name = "generation")]
        public long Generation { get; set; }

        [DataMember(Name = "enabled")]
        public bool Enabled { get; set; }

        [DataMember(Name = "topBarHwnd")]
        public long TopBarHwnd { get; set; }

        [DataMember(Name = "items")]
        public List<BridgedNotificationRecord> Items { get; set; }
    }

    [DataContract]
    internal sealed class BridgedNotificationRecord
    {
        [DataMember(Name = "guid")]
        public string Guid { get; set; }

        [DataMember(Name = "appKey")]
        public string AppKey { get; set; }

        [DataMember(Name = "displayName")]
        public string DisplayName { get; set; }

        [DataMember(Name = "title")]
        public string Title { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "iconPath")]
        public string IconPath { get; set; }

        [DataMember(Name = "targetHwnd")]
        public long TargetHwnd { get; set; }

        [DataMember(Name = "launchPath")]
        public string LaunchPath { get; set; }

        [DataMember(Name = "processName")]
        public string ProcessName { get; set; }

        [DataMember(Name = "timestampMs")]
        public long TimestampMs { get; set; }

        [DataMember(Name = "episodeId")]
        public long EpisodeId { get; set; }
    }

    [DataContract]
    internal sealed class BridgedNotificationAckSnapshot
    {
        [DataMember(Name = "generation")]
        public long Generation { get; set; }

        [DataMember(Name = "dismissed")]
        public List<BridgedNotificationAckEntry> Dismissed { get; set; }
    }

    [DataContract]
    internal sealed class BridgedNotificationAckEntry
    {
        [DataMember(Name = "guid")]
        public string Guid { get; set; }

        [DataMember(Name = "episodeId")]
        public long EpisodeId { get; set; }

        [DataMember(Name = "action")]
        public string Action { get; set; }
    }

    internal static class MessageCenterBridgeController
    {
        internal const string BridgedGuidPrefix = "mvt-app-";
        internal const string SyncWindowMessageName = "MagicVisionToolbox_SyncNotifications";

        private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "MagicTaskbar");
        private static readonly string BridgeStatePath = Path.Combine(TempDir, "bridged-notifications.json");
        private static readonly string BridgeAckPath = Path.Combine(TempDir, "bridged-notifications-ack.json");

        private static readonly object Sync = new object();
        private static readonly Dictionary<IntPtr, Window> HookedWindows = new Dictionary<IntPtr, Window>();
        private static readonly HashSet<Button> HookedClearButtons = new HashSet<Button>();
        private static readonly HashSet<FrameworkElement> HookedCards = new HashSet<FrameworkElement>();
        private static readonly Dictionary<string, BridgedNotificationRecord> ActiveRecords = new Dictionary<string, BridgedNotificationRecord>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> InjectedEpisodeByGuid = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> LocalDismissedEpisodes = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ImageSource> IconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        private static bool _reflectionInitialized;
        private static MethodInfo _messageVmGetInstanceMethod;
        private static MethodInfo _messageVmAddMethod;
        private static MethodInfo _messageVmRemoveByGuidMethod;
        private static FieldInfo _messageVmMessagesField;
        private static Type _superNotifType;
        private static Type _dataItemType3Type;
        private static Type _tipsTypeEnum;
        private static Type _tipsModeEnum;
        private static Type _tipsLevelEnum;
        private static Type _showModeEnum;
        private static Type _foldStatusEnum;
        private static Type _notifTypeEnum;
        private static Type _tipsSuperControlType;
        private static Type _notifItemNormalType;
        private static MethodInfo _hideWndMethod;

        private static FileSystemWatcher _watcher;
        private static DispatcherTimer _pollTimer;
        private static Window _primaryWindow;
        private static uint _syncMsgId;
        private static long _lastObservedGeneration = -1;
        private static long _topBarHwnd;
        private static long _ackGeneration;
        private static Point _cardMouseDownPoint;
        private static bool _hasCardMouseDownPoint;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessageW(string lpString);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(uint dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        internal static bool IsHostWindowType(Window w)
        {
            if (w == null)
            {
                return false;
            }

            string fullName = w.GetType().FullName;
            return string.Equals(fullName, "MessageCenterUI.MainWindowAI", StringComparison.Ordinal) ||
                   string.Equals(fullName, "MessageCenterUI.MainWindow", StringComparison.Ordinal);
        }

        internal static void AttachWindow(Window w)
        {
            if (w == null)
            {
                return;
            }

            EnsureReflectionInitialized();
            if (_syncMsgId == 0)
            {
                _syncMsgId = RegisterWindowMessageW(SyncWindowMessageName);
            }

            lock (Sync)
            {
                if (_primaryWindow == null || string.Equals(w.GetType().FullName, "MessageCenterUI.MainWindowAI", StringComparison.Ordinal))
                {
                    _primaryWindow = w;
                }
            }

            EnsureHwndHook(w);
            HookClearButton(w);
            EnsureSyncInfrastructure(w.Dispatcher);
            SyncFromDiskOnUiThread();
        }

        internal static void OnElementLoaded(FrameworkElement fe)
        {
            if (fe == null)
            {
                return;
            }

            EnsureReflectionInitialized();
            if (_tipsSuperControlType != null && _tipsSuperControlType.IsInstanceOfType(fe))
            {
                AttachTipsSuperNotificationCard(fe);
                return;
            }

            if (_notifItemNormalType != null && _notifItemNormalType.IsInstanceOfType(fe))
            {
                FixLoadedNotificationIcon(fe, "dataContext", "LargeIconPath", "imageTitleIcon");
                return;
            }

            Button btn = fe as Button;
            if (btn != null && (string.Equals(btn.Name, "ClearButton", StringComparison.Ordinal) ||
                                string.Equals(btn.Name, "ClearAllButton", StringComparison.Ordinal)))
            {
                AttachClearButton(btn);
            }
        }

        private static void EnsureReflectionInitialized()
        {
            if (_reflectionInitialized)
            {
                return;
            }

            lock (Sync)
            {
                if (_reflectionInitialized)
                {
                    return;
                }

                Assembly asm = Assembly.GetEntryAssembly();
                if (asm == null)
                {
                    return;
                }

                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

                Type messageVmType = asm.GetType("MessageCenterUI.ViewModel.MessageVM");
                if (messageVmType != null)
                {
                    _messageVmGetInstanceMethod = messageVmType.GetMethod("GetInstance", BindingFlags.Public | BindingFlags.Static);
                    _messageVmAddMethod = messageVmType.GetMethod("Add", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _messageVmMessagesField = messageVmType.GetField("messages", flags);
                    MethodInfo[] methods = messageVmType.GetMethods(flags);
                    for (int i = 0; i < methods.Length; i++)
                    {
                        if (string.Equals(methods[i].Name, "Remove", StringComparison.Ordinal))
                        {
                            ParameterInfo[] ps = methods[i].GetParameters();
                            if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                            {
                                _messageVmRemoveByGuidMethod = methods[i];
                                break;
                            }
                        }
                    }
                }

                _superNotifType = asm.GetType("MessageCenterUI.Model.SuperNotificationMessage");
                _dataItemType3Type = asm.GetType("MessageCenterUI.Model.DataItemType3");
                _tipsTypeEnum = asm.GetType("MessageCenterUI.ConstDefine+TIPS_TYPE");
                _tipsModeEnum = asm.GetType("MessageCenterUI.ConstDefine+TIPS_MODE");
                _tipsLevelEnum = asm.GetType("MessageCenterUI.ConstDefine+TIPS_LEVEL");
                _showModeEnum = asm.GetType("MessageCenterUI.ConstDefine+SHOW_MODE");
                _foldStatusEnum = asm.GetType("MessageCenterUI.ConstDefine+FOLD_STATUS");
                _notifTypeEnum = asm.GetType("MessageCenterUI.ConstDefine+NotificationType");
                _tipsSuperControlType = asm.GetType("MessageCenterUI.View.Controls.TipsSuperNotification");
                _notifItemNormalType = asm.GetType("MessageCenterUI.View.Controls.SuperNotifItemNormal");

                Type mwAiType = asm.GetType("MessageCenterUI.MainWindowAI");
                if (mwAiType != null)
                {
                    _hideWndMethod = mwAiType.GetMethod("HideWnd", flags);
                }

                _reflectionInitialized = _messageVmGetInstanceMethod != null &&
                                         _messageVmAddMethod != null &&
                                         _superNotifType != null &&
                                         _dataItemType3Type != null;
                BridgeLog.Info("Reflection initialized: ready=" + _reflectionInitialized);
            }
        }

        private static void EnsureHwndHook(Window w)
        {
            var interop = new WindowInteropHelper(w);
            IntPtr hwnd = interop.Handle;
            if (hwnd == IntPtr.Zero)
            {
                w.SourceInitialized -= OnWindowSourceInitialized;
                w.SourceInitialized += OnWindowSourceInitialized;
                return;
            }

            lock (Sync)
            {
                if (HookedWindows.ContainsKey(hwnd))
                {
                    return;
                }

                HwndSource source = HwndSource.FromHwnd(hwnd);
                if (source == null)
                {
                    return;
                }

                source.AddHook(WindowWndProc);
                HookedWindows[hwnd] = w;
                BridgeLog.Info("Hooked MessageCenter HWND=0x" + hwnd.ToString("X"));
            }
        }

        private static void OnWindowSourceInitialized(object sender, EventArgs e)
        {
            Window w = sender as Window;
            if (w != null)
            {
                w.SourceInitialized -= OnWindowSourceInitialized;
                EnsureHwndHook(w);
            }
        }

        private static IntPtr WindowWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_syncMsgId != 0 && (uint)msg == _syncMsgId)
            {
                SyncFromDiskOnUiThread();
            }

            return IntPtr.Zero;
        }

        private static void HookClearButton(Window w)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                FieldInfo clearBtnField = w.GetType().GetField("ClearButton", flags);
                Button btn = clearBtnField != null ? clearBtnField.GetValue(w) as Button : null;
                if (btn != null)
                {
                    AttachClearButton(btn);
                }
            }
            catch
            {
                // Best effort.
            }
        }

        private static void AttachClearButton(Button btn)
        {
            if (btn == null)
            {
                return;
            }

            lock (Sync)
            {
                if (!HookedClearButtons.Add(btn))
                {
                    return;
                }
            }

            btn.PreviewMouseLeftButtonUp += OnClearAllPreviewMouseLeftButtonUp;
        }

        private static void OnClearAllPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                DismissAllBridgedCards("dismiss");
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("OnClearAllPreviewMouseLeftButtonUp failed: " + ex.Message);
            }
        }

        private static void EnsureSyncInfrastructure(Dispatcher dispatcher)
        {
            if (_pollTimer == null && dispatcher != null)
            {
                _pollTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(900)
                };
                _pollTimer.Tick += delegate { SyncFromDiskOnUiThread(); };
                _pollTimer.Start();
            }

            if (_watcher == null)
            {
                try
                {
                    Directory.CreateDirectory(TempDir);
                    _watcher = new FileSystemWatcher(TempDir, "bridged-notifications.json")
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size | NotifyFilters.FileName,
                        EnableRaisingEvents = true
                    };
                    FileSystemEventHandler onFsChanged = delegate
                    {
                        Dispatcher d = dispatcher;
                        if (d != null && !d.HasShutdownStarted)
                        {
                            d.BeginInvoke(DispatcherPriority.Normal, new Action(SyncFromDiskOnUiThread));
                        }
                    };
                    _watcher.Changed += onFsChanged;
                    _watcher.Created += onFsChanged;
                    _watcher.Deleted += onFsChanged;
                    _watcher.Renamed += delegate { onFsChanged(null, null); };
                }
                catch (Exception ex)
                {
                    BridgeLog.Warn("FileSystemWatcher init skipped: " + ex.Message);
                }
            }
        }

        internal static void SyncFromDiskOnUiThread()
        {
            try
            {
                EnsureReflectionInitialized();
                if (!_reflectionInitialized)
                {
                    return;
                }

                BridgedNotificationSnapshot snapshot = ReadBridgeSnapshot();
                if (snapshot == null)
                {
                    RemoveAllInjectedCardsFromVm();
                    lock (Sync)
                    {
                        ActiveRecords.Clear();
                    }
                    return;
                }

                lock (Sync)
                {
                    _topBarHwnd = snapshot.TopBarHwnd;
                    _lastObservedGeneration = snapshot.Generation;
                }

                if (!snapshot.Enabled || snapshot.Items == null || snapshot.Items.Count == 0)
                {
                    RemoveAllInjectedCardsFromVm();
                    lock (Sync)
                    {
                        ActiveRecords.Clear();
                    }
                    return;
                }

                var desiredByGuid = new Dictionary<string, BridgedNotificationRecord>(StringComparer.Ordinal);
                for (int i = 0; i < snapshot.Items.Count; i++)
                {
                    BridgedNotificationRecord r = snapshot.Items[i];
                    if (r == null || string.IsNullOrEmpty(r.Guid) || !r.Guid.StartsWith(BridgedGuidPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    long locallyDismissedEp;
                    lock (Sync)
                    {
                        if (LocalDismissedEpisodes.TryGetValue(r.Guid, out locallyDismissedEp) && locallyDismissedEp == r.EpisodeId)
                        {
                            continue;
                        }
                    }

                    desiredByGuid[r.Guid] = r;
                }

                object vm = _messageVmGetInstanceMethod.Invoke(null, null);
                if (vm == null)
                {
                    return;
                }

                // Remove bridged cards that were cleared because the user read the app or disabled the bridge.
                List<string> staleGuids = new List<string>();
                lock (Sync)
                {
                    foreach (KeyValuePair<string, long> kv in InjectedEpisodeByGuid)
                    {
                        if (!desiredByGuid.ContainsKey(kv.Key))
                        {
                            staleGuids.Add(kv.Key);
                        }
                    }

                    // Also remove stale dismissed entries whose episode is no longer in snapshot.
                    List<string> expiredDismissed = null;
                    foreach (KeyValuePair<string, long> kv in LocalDismissedEpisodes)
                    {
                        bool stillSame = false;
                        for (int i = 0; i < snapshot.Items.Count; i++)
                        {
                            if (snapshot.Items[i] != null &&
                                string.Equals(snapshot.Items[i].Guid, kv.Key, StringComparison.Ordinal) &&
                                snapshot.Items[i].EpisodeId == kv.Value)
                            {
                                stillSame = true;
                                break;
                            }
                        }

                        if (!stillSame)
                        {
                            if (expiredDismissed == null) expiredDismissed = new List<string>();
                            expiredDismissed.Add(kv.Key);
                        }
                    }

                    if (expiredDismissed != null)
                    {
                        for (int i = 0; i < expiredDismissed.Count; i++)
                        {
                            LocalDismissedEpisodes.Remove(expiredDismissed[i]);
                        }
                    }
                }

                for (int i = 0; i < staleGuids.Count; i++)
                {
                    RemoveCardFromVm(vm, staleGuids[i]);
                    lock (Sync)
                    {
                        ActiveRecords.Remove(staleGuids[i]);
                    }
                }

                IDictionary vmDict = _messageVmMessagesField != null ? _messageVmMessagesField.GetValue(vm) as IDictionary : null;

                foreach (KeyValuePair<string, BridgedNotificationRecord> kv in desiredByGuid)
                {
                    BridgedNotificationRecord rec = kv.Value;
                    long existingEpisode = -1;
                    bool tracked;
                    lock (Sync)
                    {
                        ActiveRecords[rec.Guid] = rec;
                        tracked = InjectedEpisodeByGuid.TryGetValue(rec.Guid, out existingEpisode);
                    }

                    bool presentInVm = vmDict == null || vmDict.Contains(rec.Guid);
                    if (tracked && existingEpisode == rec.EpisodeId && presentInVm)
                    {
                        continue;
                    }

                    if (presentInVm && tracked && existingEpisode != rec.EpisodeId)
                    {
                        RemoveCardFromVm(vm, rec.Guid);
                    }

                    object nativeMsg = BuildSuperNotificationMessage(rec);
                    if (nativeMsg != null)
                    {
                        _messageVmAddMethod.Invoke(vm, new object[] { nativeMsg });
                        lock (Sync)
                        {
                            InjectedEpisodeByGuid[rec.Guid] = rec.EpisodeId;
                        }
                        BridgeLog.Info("Added bridged card: guid=" + rec.Guid + " episode=" + rec.EpisodeId + " app=" + rec.DisplayName);
                    }
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("SyncFromDiskOnUiThread failed: " + ex.Message);
            }
        }

        private static void RemoveAllInjectedCardsFromVm()
        {
            List<string> guids;
            lock (Sync)
            {
                guids = new List<string>(InjectedEpisodeByGuid.Keys);
            }

            if (guids.Count == 0 || _messageVmGetInstanceMethod == null)
            {
                return;
            }

            object vm = _messageVmGetInstanceMethod.Invoke(null, null);
            if (vm == null)
            {
                return;
            }

            for (int i = 0; i < guids.Count; i++)
            {
                RemoveCardFromVm(vm, guids[i]);
            }
        }

        private static void RemoveCardFromVm(object vm, string guid)
        {
            if (vm != null && _messageVmRemoveByGuidMethod != null && !string.IsNullOrEmpty(guid))
            {
                _messageVmRemoveByGuidMethod.Invoke(vm, new object[] { guid });
            }

            lock (Sync)
            {
                InjectedEpisodeByGuid.Remove(guid);
            }
        }

        private static object BuildSuperNotificationMessage(BridgedNotificationRecord rec)
        {
            if (_superNotifType == null || _dataItemType3Type == null)
            {
                return null;
            }

            string iconPath = !string.IsNullOrEmpty(rec.IconPath) && File.Exists(rec.IconPath) ? rec.IconPath : string.Empty;
            ulong whenMs = rec.TimestampMs > 0 ? (ulong)rec.TimestampMs : (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long sec = (long)(whenMs / 1000UL);

            object item = Activator.CreateInstance(_dataItemType3Type);
            SetProp(item, "NotificationType", _notifTypeEnum != null ? Enum.ToObject(_notifTypeEnum, 0) : 0);
            SetProp(item, "UniqId", rec.Guid + ":" + rec.EpisodeId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SetProp(item, "NodeId", "0");
            SetProp(item, "SbnKey", rec.Guid);
            SetProp(item, "UniqMsgId", rec.EpisodeId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SetProp(item, "When", whenMs);
            SetProp(item, "Title", string.IsNullOrEmpty(rec.Title) ? rec.DisplayName : rec.Title);
            SetProp(item, "Description", string.IsNullOrEmpty(rec.Description) ? "收到新未读消息，点击跳转查看" : rec.Description);
            SetProp(item, "LargeIconPath", iconPath);
            SetProp(item, "ActionResponseBtnIndex", -1);
            SetProp(item, "ActionResponseText", string.Empty);

            Type listType = typeof(List<>).MakeGenericType(_dataItemType3Type);
            IList items = (IList)Activator.CreateInstance(listType);
            items.Add(item);

            object msg = Activator.CreateInstance(_superNotifType);
            SetProp(msg, "Type", _tipsTypeEnum != null ? Enum.ToObject(_tipsTypeEnum, 9) : 9);
            SetProp(msg, "Mode", _tipsModeEnum != null ? Enum.ToObject(_tipsModeEnum, 0) : 0);
            SetProp(msg, "Level", _tipsLevelEnum != null ? Enum.ToObject(_tipsLevelEnum, 0) : 0);
            SetProp(msg, "ShowMode", _showModeEnum != null ? Enum.ToObject(_showModeEnum, 0) : 0);
            SetProp(msg, "Guid", rec.Guid);
            SetProp(msg, "GuidDes", string.Empty);
            SetProp(msg, "Timestamp", sec);
            SetProp(msg, "TitleTxt", rec.DisplayName ?? rec.Title ?? rec.AppKey);
            SetProp(msg, "TitleIcon", iconPath);
            SetProp(msg, "HideClose", false);
            SetProp(msg, "UniqPkgName", "mvt.app." + (rec.AppKey ?? "app"));
            SetProp(msg, "NewestWhen", whenMs);
            SetProp(msg, "FoldStatus", _foldStatusEnum != null ? Enum.ToObject(_foldStatusEnum, 1) : 1);
            SetProp(msg, "IsPureSmallIcon", false);
            SetProp(msg, "ItemsSuper", items);
            return msg;
        }

        private static void AttachTipsSuperNotificationCard(FrameworkElement card)
        {
            string guid = ExtractCardGuid(card);
            if (string.IsNullOrEmpty(guid) || !guid.StartsWith(BridgedGuidPrefix, StringComparison.Ordinal))
            {
                return;
            }

            FixLoadedNotificationIcon(card, "data_context", "TitleIcon", "imageTitleIcon");

            lock (Sync)
            {
                if (!HookedCards.Add(card))
                {
                    return;
                }
            }

            card.PreviewMouseLeftButtonDown += OnBridgedCardPreviewMouseLeftButtonDown;
            card.PreviewMouseLeftButtonUp += OnBridgedCardPreviewMouseLeftButtonUp;
        }

        private static void FixLoadedNotificationIcon(FrameworkElement control, string contextFieldName, string pathPropName, string imageFieldName)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                Type t = control.GetType();
                FieldInfo ctxField = t.GetField(contextFieldName, flags);
                object ctx = ctxField != null ? ctxField.GetValue(control) : control.DataContext;
                if (ctx == null)
                {
                    return;
                }

                PropertyInfo pathProp = ctx.GetType().GetProperty(pathPropName, flags);
                string iconPath = pathProp != null ? pathProp.GetValue(ctx, null) as string : null;
                if (string.IsNullOrEmpty(iconPath) || !iconPath.StartsWith(TempDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(iconPath))
                {
                    return;
                }

                FieldInfo imgField = t.GetField(imageFieldName, flags);
                Image img = imgField != null ? imgField.GetValue(control) as Image : null;
                if (img != null && img.Source == null)
                {
                    ImageSource src = GetCachedBitmap(iconPath);
                    if (src != null)
                    {
                        img.Source = src;
                    }
                }
            }
            catch
            {
                // Ignore best-effort icon fallback.
            }
        }

        private static ImageSource GetCachedBitmap(string path)
        {
            lock (Sync)
            {
                ImageSource cached;
                if (IconCache.TryGetValue(path, out cached))
                {
                    return cached;
                }
            }

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();

                lock (Sync)
                {
                    IconCache[path] = bmp;
                }
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static void OnBridgedCardPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            FrameworkElement card = sender as FrameworkElement;
            string guid = ExtractCardGuid(card);
            if (string.IsNullOrEmpty(guid) || !guid.StartsWith(BridgedGuidPrefix, StringComparison.Ordinal))
            {
                return;
            }

            _cardMouseDownPoint = e.GetPosition(card);
            _hasCardMouseDownPoint = true;

            if (IsDescendantOfNamedControl(e.OriginalSource as DependencyObject, "gridClose", "btnClose"))
            {
                // Suppress Honor SetUIReply for synthetic bridged cards.
                e.Handled = true;
            }
        }

        private static void OnBridgedCardPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            FrameworkElement card = sender as FrameworkElement;
            string guid = ExtractCardGuid(card);
            if (string.IsNullOrEmpty(guid) || !guid.StartsWith(BridgedGuidPrefix, StringComparison.Ordinal))
            {
                return;
            }

            if (IsDescendantOfNamedControl(e.OriginalSource as DependencyObject, "gridFold", "btnFold"))
            {
                return;
            }

            bool isCloseClick = IsDescendantOfNamedControl(e.OriginalSource as DependencyObject, "gridClose", "btnClose");
            if (!isCloseClick && _hasCardMouseDownPoint)
            {
                Point up = e.GetPosition(card);
                if (Math.Abs(up.X - _cardMouseDownPoint.X) > 10.0 || Math.Abs(up.Y - _cardMouseDownPoint.Y) > 10.0)
                {
                    _hasCardMouseDownPoint = false;
                    return;
                }
            }

            _hasCardMouseDownPoint = false;
            e.Handled = true;

            BridgedNotificationRecord rec;
            lock (Sync)
            {
                ActiveRecords.TryGetValue(guid, out rec);
            }

            long episodeId = rec != null ? rec.EpisodeId : 0;
            if (isCloseClick)
            {
                DismissSingleCard(guid, episodeId, "dismiss", activateTarget: false, rec: null);
            }
            else
            {
                DismissSingleCard(guid, episodeId, "activate", activateTarget: true, rec: rec);
            }
        }

        private static void DismissSingleCard(string guid, long episodeId, string action, bool activateTarget, BridgedNotificationRecord rec)
        {
            lock (Sync)
            {
                if (episodeId > 0)
                {
                    LocalDismissedEpisodes[guid] = episodeId;
                }
                ActiveRecords.Remove(guid);
            }

            object vm = _messageVmGetInstanceMethod != null ? _messageVmGetInstanceMethod.Invoke(null, null) : null;
            RemoveCardFromVm(vm, guid);

            WriteAckSnapshot(new List<BridgedNotificationAckEntry>
            {
                new BridgedNotificationAckEntry
                {
                    Guid = guid,
                    EpisodeId = episodeId,
                    Action = action
                }
            });

            NotifyTopBarProcess();

            if (activateTarget && rec != null)
            {
                HidePrimaryMessageCenterWindow();
                ActivateTargetApp(rec);
            }

            BridgeLog.Info("Handled bridged card " + action + ": guid=" + guid + " episode=" + episodeId);
        }

        private static void DismissAllBridgedCards(string action)
        {
            List<KeyValuePair<string, long>> toDismiss = new List<KeyValuePair<string, long>>();
            lock (Sync)
            {
                foreach (KeyValuePair<string, long> kv in InjectedEpisodeByGuid)
                {
                    toDismiss.Add(kv);
                    LocalDismissedEpisodes[kv.Key] = kv.Value;
                    ActiveRecords.Remove(kv.Key);
                }
            }

            if (toDismiss.Count == 0)
            {
                return;
            }

            object vm = _messageVmGetInstanceMethod != null ? _messageVmGetInstanceMethod.Invoke(null, null) : null;
            List<BridgedNotificationAckEntry> entries = new List<BridgedNotificationAckEntry>();
            for (int i = 0; i < toDismiss.Count; i++)
            {
                string guid = toDismiss[i].Key;
                long ep = toDismiss[i].Value;
                RemoveCardFromVm(vm, guid);
                entries.Add(new BridgedNotificationAckEntry
                {
                    Guid = guid,
                    EpisodeId = ep,
                    Action = action
                });
            }

            WriteAckSnapshot(entries);
            NotifyTopBarProcess();
            BridgeLog.Info("Cleared all bridged cards (count=" + entries.Count + ").");
        }

        private static void HidePrimaryMessageCenterWindow()
        {
            try
            {
                Window w;
                lock (Sync)
                {
                    w = _primaryWindow;
                }

                if (w != null && _hideWndMethod != null && string.Equals(w.GetType().FullName, "MessageCenterUI.MainWindowAI", StringComparison.Ordinal))
                {
                    _hideWndMethod.Invoke(w, new object[] { true });
                }
                else if (w != null)
                {
                    w.Hide();
                }
            }
            catch
            {
                // Ignore window hide failure.
            }
        }

        private static void ActivateTargetApp(BridgedNotificationRecord rec)
        {
            try
            {
                IntPtr target = rec.TargetHwnd != 0 ? new IntPtr(rec.TargetHwnd) : IntPtr.Zero;
                if (target != IntPtr.Zero && IsWindow(target))
                {
                    ForceForegroundWindow(target);
                    return;
                }

                string procName = rec.ProcessName;
                if (string.IsNullOrEmpty(procName) && !string.IsNullOrEmpty(rec.LaunchPath))
                {
                    procName = Path.GetFileNameWithoutExtension(rec.LaunchPath);
                }

                if (!string.IsNullOrEmpty(procName))
                {
                    Process[] procs = Process.GetProcessesByName(procName);
                    for (int i = 0; i < procs.Length; i++)
                    {
                        using (procs[i])
                        {
                            IntPtr mainHwnd = procs[i].MainWindowHandle;
                            if (mainHwnd != IntPtr.Zero && IsWindow(mainHwnd))
                            {
                                ForceForegroundWindow(mainHwnd);
                                return;
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(rec.LaunchPath) && File.Exists(rec.LaunchPath))
                {
                    Process.Start(new ProcessStartInfo(rec.LaunchPath) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("ActivateTargetApp failed: " + ex.Message);
            }
        }

        private static void ForceForegroundWindow(IntPtr target)
        {
            uint pid;
            uint targetThread = GetWindowThreadProcessId(target, out pid);
            if (pid != 0)
            {
                AllowSetForegroundWindow(pid);
            }

            if (IsIconic(target))
            {
                ShowWindow(target, 9); // SW_RESTORE
            }
            else
            {
                ShowWindow(target, 5); // SW_SHOW
            }

            IntPtr fg = GetForegroundWindow();
            uint unusedPid;
            uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out unusedPid) : 0;
            uint curThread = GetCurrentThreadId();
            bool attachedFg = false;
            bool attachedTarget = false;
            try
            {
                if (fgThread != 0 && fgThread != curThread)
                {
                    attachedFg = AttachThreadInput(curThread, fgThread, true);
                }
                if (targetThread != 0 && targetThread != curThread && targetThread != fgThread)
                {
                    attachedTarget = AttachThreadInput(curThread, targetThread, true);
                }

                BringWindowToTop(target);
                SetForegroundWindow(target);
            }
            finally
            {
                if (attachedTarget)
                {
                    AttachThreadInput(curThread, targetThread, false);
                }
                if (attachedFg)
                {
                    AttachThreadInput(curThread, fgThread, false);
                }
            }
        }

        private static string ExtractCardGuid(FrameworkElement card)
        {
            if (card == null)
            {
                return null;
            }

            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                object dc = card.DataContext;
                if (dc == null)
                {
                    FieldInfo f = card.GetType().GetField("data_context", flags);
                    if (f != null)
                    {
                        dc = f.GetValue(card);
                    }
                }

                if (dc == null)
                {
                    return null;
                }

                PropertyInfo p = dc.GetType().GetProperty("Guid", flags);
                return p != null ? p.GetValue(dc, null) as string : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsDescendantOfNamedControl(DependencyObject source, string name1, string name2)
        {
            DependencyObject cur = source;
            while (cur != null)
            {
                FrameworkElement fe = cur as FrameworkElement;
                if (fe != null)
                {
                    string n = fe.Name;
                    if (string.Equals(n, name1, StringComparison.Ordinal) ||
                        string.Equals(n, name2, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                cur = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur);
            }

            return false;
        }

        private static void NotifyTopBarProcess()
        {
            if (_syncMsgId == 0)
            {
                _syncMsgId = RegisterWindowMessageW(SyncWindowMessageName);
            }

            IntPtr hwnd = IntPtr.Zero;
            lock (Sync)
            {
                if (_topBarHwnd != 0)
                {
                    hwnd = new IntPtr(_topBarHwnd);
                }
            }

            if (hwnd != IntPtr.Zero && IsWindow(hwnd))
            {
                PostMessageW(hwnd, _syncMsgId, IntPtr.Zero, IntPtr.Zero);
                return;
            }

            try
            {
                Process[] procs = Process.GetProcessesByName("magictaskbar-ui");
                for (int i = 0; i < procs.Length; i++)
                {
                    using (procs[i])
                    {
                        if (procs[i].MainWindowHandle != IntPtr.Zero)
                        {
                            PostMessageW(procs[i].MainWindowHandle, _syncMsgId, IntPtr.Zero, IntPtr.Zero);
                        }
                    }
                }
            }
            catch
            {
                // Best effort wakeup; TopBar also checks on timer.
            }
        }

        private static BridgedNotificationSnapshot ReadBridgeSnapshot()
        {
            if (!File.Exists(BridgeStatePath))
            {
                return null;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(BridgeStatePath);
                    if (bytes.Length == 0)
                    {
                        return null;
                    }

                    using (var ms = new MemoryStream(bytes))
                    {
                        var ser = new DataContractJsonSerializer(typeof(BridgedNotificationSnapshot));
                        return ser.ReadObject(ms) as BridgedNotificationSnapshot;
                    }
                }
                catch
                {
                    Thread.Sleep(20);
                }
            }

            return null;
        }

        private static void WriteAckSnapshot(List<BridgedNotificationAckEntry> newEntries)
        {
            if (newEntries == null || newEntries.Count == 0)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(TempDir);
                BridgedNotificationAckSnapshot ack = null;
                if (File.Exists(BridgeAckPath))
                {
                    try
                    {
                        byte[] existingBytes = File.ReadAllBytes(BridgeAckPath);
                        if (existingBytes.Length > 0)
                        {
                            using (var ms = new MemoryStream(existingBytes))
                            {
                                var ser = new DataContractJsonSerializer(typeof(BridgedNotificationAckSnapshot));
                                ack = ser.ReadObject(ms) as BridgedNotificationAckSnapshot;
                            }
                        }
                    }
                    catch
                    {
                        ack = null;
                    }
                }

                if (ack == null)
                {
                    ack = new BridgedNotificationAckSnapshot();
                }

                if (ack.Dismissed == null)
                {
                    ack.Dismissed = new List<BridgedNotificationAckEntry>();
                }

                for (int i = 0; i < newEntries.Count; i++)
                {
                    BridgedNotificationAckEntry entry = newEntries[i];
                    bool updated = false;
                    for (int j = 0; j < ack.Dismissed.Count; j++)
                    {
                        if (string.Equals(ack.Dismissed[j].Guid, entry.Guid, StringComparison.Ordinal))
                        {
                            ack.Dismissed[j] = entry;
                            updated = true;
                            break;
                        }
                    }

                    if (!updated)
                    {
                        ack.Dismissed.Add(entry);
                    }
                }

                if (ack.Dismissed.Count > 32)
                {
                    ack.Dismissed.RemoveRange(0, ack.Dismissed.Count - 32);
                }

                ack.Generation = Interlocked.Increment(ref _ackGeneration);

                string tmp = BridgeAckPath + ".tmp";
                using (var ms = new MemoryStream())
                {
                    var ser = new DataContractJsonSerializer(typeof(BridgedNotificationAckSnapshot));
                    ser.WriteObject(ms, ack);
                    File.WriteAllBytes(tmp, ms.ToArray());
                }

                File.Copy(tmp, BridgeAckPath, true);
                try { File.Delete(tmp); } catch { }
            }
            catch (Exception ex)
            {
                BridgeLog.Warn("WriteAckSnapshot failed: " + ex.Message);
            }
        }

        private static void SetProp(object target, string propName, object value)
        {
            if (target == null)
            {
                return;
            }

            PropertyInfo prop = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(target, value, null);
            }
        }
    }

    internal static class BridgeLog
    {
        private const long MaxLogBytes = 512 * 1024;
        private static readonly object LogLock = new object();
        private static readonly string LogDir = Path.Combine(Path.GetTempPath(), "MagicTaskbar");
        private static readonly string LogPath = Path.Combine(LogDir, "message-center-bridge.log");

        internal static void Info(string message) { Write("INFO", message); }
        internal static void Warn(string message) { Write("WARN", message); }

        private static void Write(string level, string message)
        {
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] [PID=" + Process.GetCurrentProcess().Id + "] " + message + Environment.NewLine;
                lock (LogLock)
                {
                    Directory.CreateDirectory(LogDir);
                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > MaxLogBytes)
                    {
                        File.Delete(LogPath);
                    }
                    File.AppendAllText(LogPath, line, Encoding.UTF8);
                }
            }
            catch
            {
                // Ignore logging failures.
            }
        }
    }
}

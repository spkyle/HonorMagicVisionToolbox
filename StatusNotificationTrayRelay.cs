using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TopBarAdaptiveColor
{
    /// <summary>
    /// Relays clicks on TopBar StatusNotification icons (WeChat, Feishu, QQ, DingTalk,
    /// WeCom, WeMeet, CloudHub) and custom user-pinned hidden tray icons directly IN PLACE
    /// to each app's native tray callback window via PostMessage — eliminating overflow flyout
    /// flashing and cursor teleportation.
    /// Hold Shift + Right-Click (or disable the toggle / if the app isn't running) to use Honor's menu.
    /// </summary>
    internal sealed class StatusNotificationTrayRelay : IDisposable
    {
        private const uint WmMouseMove = 0x0200;
        private const uint WmLButtonDown = 0x0201;
        private const uint WmLButtonUp = 0x0202;
        private const uint WmLButtonDblClk = 0x0203;
        private const uint WmRButtonDown = 0x0204;
        private const uint WmRButtonUp = 0x0205;
        private const uint WmContextMenu = 0x007B;
        private const uint NinSelect = 0x0400;
        private static readonly IntPtr HwndMessage = new(-3);
        private static readonly long NotifyIconCacheTtlTicks = TimeSpan.FromSeconds(30).Ticks;

        private sealed record CachedTrayTarget(
            IntPtr Hwnd,
            uint Pid,
            string ClassName,
            uint? Uid,
            uint CallbackMessage,
            bool IsNotifyIconV4);

        private sealed record NotifyIconSettingEntry(
            string SubKeyName,
            string ExecutablePath,
            string FileNameWithoutExtension,
            uint Uid);

        private static readonly object RegistrationSync = new();
        private static int _activeInstanceCount;
        private static int _prewarmScheduled;

        private static readonly ConcurrentDictionary<string, CachedTrayTarget> KnownTargetCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, CachedTrayTarget> ArbitraryTargetCache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly object RegistryCacheSync = new();
        private static IReadOnlyList<NotifyIconSettingEntry>? _notifyIconEntriesCache;
        private static long _notifyIconEntriesCacheTicksUtc;
        private static long _suppressWorkspaceOverlapUntilTicksUtc;

        private readonly Window _topBarWindow;
        private readonly ItemsControl? _itemsControl;
        private readonly MethodInfo? _closePopupsExceptMethod;
        private readonly object? _popupSurfaceNone;

        private object? _workspaceReservationHost;
        private FieldInfo? _visualVisibleField;
        private FieldInfo? _overlapField;
        private Func<bool>? _origVisualVisible;
        private object? _hookedOverlapSubscription;
        private FieldInfo? _subVisibleField;
        private FieldInfo? _subPublishField;
        private Func<bool>? _origSubVisible;
        private Func<bool>? _origSubPublish;
        private static object? _sharedOverlapCoordinator;
        private static MethodInfo? _cancelPendingOverlapMethod;

        private readonly MouseButtonEventHandler _previewRightUpHandler;
        private bool _skipNextRelay;
        private int _relayGeneration;
        private int _disposedFlag;

        private bool _disposed => Volatile.Read(ref _disposedFlag) != 0;

        internal sealed class TrayAppProtocol
        {
            public string AppKey { get; init; } = string.Empty;
            public string[] ProcessNames { get; init; } = Array.Empty<string>();
            public string ClassPattern { get; init; } = string.Empty;
            public bool ClassExactMatch { get; init; }
            public string? ClassSecondaryToken { get; init; }
            public uint CallbackMessage { get; init; }
            public uint DefaultUid { get; init; }
            public bool IsNotifyIconV4 { get; init; }
        }

        private static readonly TrayAppProtocol[] KnownApps =
        {
            new()
            {
                AppKey = "wechat",
                ProcessNames = new[] { "weixin", "wechat" },
                ClassPattern = "WxTrayIconMessageWindowClass",
                ClassExactMatch = false,
                CallbackMessage = 0x8385, // 33669 (from WeChatTrayActivation / Weixin.dll system_tray_icon_win)
                DefaultUid = 0,
                // Weixin.dll (4.x Qt tray) requires NOTIFYICON_VERSION_4: it ignores 0x0204/0x0205
                // and only opens its context menu on WM_CONTEXTMENU (0x007B) with MAKEWPARAM(x,y) and HIWORD(lParam)==0.
                IsNotifyIconV4 = true
            },
            new()
            {
                AppKey = "feishu",
                ProcessNames = new[] { "feishu", "lark" },
                ClassPattern = "Lark_StatusTrayWindow",
                ClassExactMatch = true,
                CallbackMessage = 0x8001, // 32769 (from FeishuTrayActivation)
                DefaultUid = 1000,
                IsNotifyIconV4 = false
            },
            new()
            {
                AppKey = "qq",
                ProcessNames = new[] { "qq", "tim" },
                ClassPattern = "Electron_NotifyIconHostWindow",
                ClassExactMatch = true,
                CallbackMessage = 0x8001, // 32769 (from QqTrayActivation)
                DefaultUid = 3,
                IsNotifyIconV4 = false
            },
            new()
            {
                AppKey = "dingtalk",
                ProcessNames = new[] { "dingtalk" },
                ClassPattern = "CTrayUIMgr_ClassName",
                ClassExactMatch = true,
                CallbackMessage = 0x8FA1, // 36769 (from DingTalkTrayActivation)
                DefaultUid = 1000,
                IsNotifyIconV4 = true
            },
            new()
            {
                AppKey = "wecom",
                ProcessNames = new[] { "wxwork", "wecom" },
                ClassPattern = "WeWorkWindow",
                ClassExactMatch = true,
                CallbackMessage = 0x0BD2, // 3026 (from WeComTrayActivation)
                DefaultUid = 0,
                IsNotifyIconV4 = false
            },
            new()
            {
                AppKey = "wemeet",
                ProcessNames = new[] { "wemeetapp", "wemeet" },
                ClassPattern = "QWindowIcon",
                ClassExactMatch = false,
                ClassSecondaryToken = "Notify",
                CallbackMessage = 0x047B, // 1147 (from TencentMeetingTrayActivation)
                DefaultUid = 1,
                IsNotifyIconV4 = false
            },
            new()
            {
                AppKey = "cloudhub",
                ProcessNames = new[] { "cloudhub", "yzj" },
                ClassPattern = "Electron_NotifyIconHostWindow",
                ClassExactMatch = true,
                CallbackMessage = 0x83E8, // 33768 (from CloudHubTrayActivation)
                DefaultUid = 0,
                IsNotifyIconV4 = false
            }
        };

        public StatusNotificationTrayRelay(Window topBarWindow)
        {
            _topBarWindow = topBarWindow ?? throw new ArgumentNullException(nameof(topBarWindow));

            const BindingFlags instFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            Type twType = topBarWindow.GetType();
            _itemsControl = twType.GetField("StatusNotificationItemsControl", instFlags)?.GetValue(topBarWindow) as ItemsControl;
            _closePopupsExceptMethod = twType.GetMethod("CloseTopBarPopupSurfacesExcept", instFlags);
            if (_closePopupsExceptMethod != null)
            {
                var ps = _closePopupsExceptMethod.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.IsEnum)
                {
                    _popupSurfaceNone = Enum.ToObject(ps[0].ParameterType, 0);
                }
            }

            _previewRightUpHandler = OnStatusItemsPreviewMouseRightButtonUp;
            _itemsControl?.AddHandler(UIElement.PreviewMouseRightButtonUpEvent, _previewRightUpHandler, handledEventsToo: false);

            HookWorkspaceOverlapGuard();

            lock (RegistrationSync)
            {
                _activeInstanceCount++;
                HiddenTrayPinEnhancer.RegisterCustomTrayRelay(TryRelayCustomTrayRequestAsync);
            }

            ScheduleBackgroundPrewarm();
            DiagnosticLog.Info($"StatusNotificationTrayRelay attached (directPostMessage, itemsControl={_itemsControl != null}, overlapGuard={_workspaceReservationHost != null}).");
        }

        private void OnStatusItemsPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_disposed || _skipNextRelay)
            {
                _skipNextRelay = false;
                return;
            }

            if (!AdaptiveSettingsStore.IsNativeTrayRightClickEnabled ||
                (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                return;
            }

            Button? btn = FindNotificationButton(e.OriginalSource as DependencyObject);
            if (btn == null)
            {
                return;
            }

            object? item = btn.Tag ?? btn.DataContext;
            if (item == null)
            {
                return;
            }

            var (appId, displayName, launchPath, catalogProcNames) = ReadNotificationItemMetadata(item);
            TrayAppProtocol? proto = ResolveAppProtocol(appId, displayName, launchPath, catalogProcNames);
            if (proto == null)
            {
                return;
            }

            e.Handled = true;

            int clickX;
            int clickY;
            if (GetCursorPos(out POINT curPt))
            {
                clickX = curPt.X;
                clickY = curPt.Y;
            }
            else
            {
                Point anchorScreen = btn.PointToScreen(new Point(btn.ActualWidth / 2.0, Math.Max(1.0, btn.ActualHeight)));
                clickX = (int)Math.Round(anchorScreen.X);
                clickY = (int)Math.Round(anchorScreen.Y);
            }

            int gen = Interlocked.Increment(ref _relayGeneration);
            _ = DispatchDirectInPlaceTrayRightClickAsync(btn, proto, clickX, clickY, gen);
        }

        private async Task DispatchDirectInPlaceTrayRightClickAsync(
            Button sourceButton,
            TrayAppProtocol proto,
            int clickX,
            int clickY,
            int generation)
        {
            try
            {
                CloseTopBarPopups();

                // Let WPF finish PreviewMouseRightButtonUp so the TopBar button releases cleanly
                // before the target app displays its popup menu at the current cursor coordinates.
                await Task.Yield();
                if (generation != Volatile.Read(ref _relayGeneration) || _disposed)
                {
                    return;
                }

                CachedTrayTarget? target = TryGetValidCachedKnownTarget(proto);
                if (target == null)
                {
                    target = await Task.Run(() => FindAndCacheKnownTarget(proto));
                    if (generation != Volatile.Read(ref _relayGeneration) || _disposed)
                    {
                        return;
                    }
                }

                if (target != null && target.Hwnd != IntPtr.Zero && IsWindow(target.Hwnd))
                {
                    uint uid = target.Uid ?? ResolveNotifyIconUid(proto) ?? proto.DefaultUid;
                    if (target.Pid != 0)
                    {
                        _ = AllowSetForegroundWindow(target.Pid);
                    }

                    BeginTrayPopupOverlapSuppression();
                    ResolvePopupCoordinatesForTarget(
                        clickX,
                        clickY,
                        proto.IsNotifyIconV4,
                        out int effectiveX,
                        out int effectiveY,
                        out int workAreaTopPx);

                    // Feishu's Lark_StatusTrayWindow invokes AsyncGetNotifyIconArea (Shell_NotifyIconGetRect IPC)
                    // and hover-card teardown on WM_MOUSEMOVE, while WM_RBUTTONDOWN opens CustomMenu directly.
                    bool includeMouseMove = !string.Equals(proto.AppKey, "feishu", StringComparison.OrdinalIgnoreCase);
                    bool posted = PostTrayRightClickCore(
                        target.Hwnd,
                        proto.CallbackMessage,
                        uid,
                        effectiveX,
                        effectiveY,
                        proto.IsNotifyIconV4,
                        includeMouseMove);

                    DiagnosticLog.Info(
                        $"Relayed IN-PLACE direct tray right-click for app={proto.AppKey} hwnd=0x{target.Hwnd.ToInt64():X} " +
                        $"pid={target.Pid} uid={uid} msg=0x{proto.CallbackMessage:X} v4={proto.IsNotifyIconV4} " +
                        $"move={includeMouseMove} pos=({effectiveX},{effectiveY}) raw=({clickX},{clickY}) posted={posted} (0px jump).");
                    if (posted)
                    {
                        if (string.Equals(proto.AppKey, "wechat", StringComparison.OrdinalIgnoreCase) && target.Pid != 0)
                        {
                            _ = EnsureWeChatPopupAtTopAsync(target.Pid, clickY, workAreaTopPx);
                        }
                        return;
                    }

                    KnownTargetCache.TryRemove(proto.AppKey, out _);
                }
                else
                {
                    DiagnosticLog.Info(
                        $"Tray callback window not found for app={proto.AppKey}; showing in-place remove menu.");
                }

                FallbackToHonorRemoveMenu(sourceButton);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"StatusNotificationTrayRelay failed: {ex.Message}");
                FallbackToHonorRemoveMenu(sourceButton);
            }
        }

        internal static Task<bool> TryRelayCustomTrayRequestAsync(CustomTrayClickRequest request)
        {
            return TryRelayCustomTrayClickAsync(
                request.ExecutablePath,
                request.ProcessName,
                request.DisplayName,
                request.KnownUid,
                request.IconGuid,
                request.KnownHwnd,
                request.KnownCallbackMessage,
                request.KnownIsNotifyIconV4,
                request.ScreenX,
                request.ScreenY,
                request.IsRightClick,
                request.IsDoubleClick);
        }

        internal static async Task<bool> TryRelayCustomTrayClickAsync(
            string? executablePath,
            string? processName,
            string? displayName,
            uint? knownUid,
            Guid? iconGuid,
            IntPtr knownHwnd,
            uint? knownCallbackMessage,
            bool? knownIsNotifyIconV4,
            int screenX,
            int screenY,
            bool isRightClick,
            bool isDoubleClick)
        {
            try
            {
                int clickX = screenX;
                int clickY = screenY;
                if (clickX == 0 && clickY == 0 && GetCursorPos(out POINT curPt))
                {
                    clickX = curPt.X;
                    clickY = curPt.Y;
                }

                TrayAppProtocol? proto = ResolveAppProtocol(processName, displayName, executablePath);
                if (proto != null)
                {
                    CachedTrayTarget? target = null;
                    if (knownHwnd != IntPtr.Zero && IsWindowAndMatchesKnownProtocol(knownHwnd, proto, out uint knownPid, out string knownClass))
                    {
                        uint? knownResolvedUid = knownUid ?? ResolveNotifyIconUidForApp(iconGuid, executablePath, proto.ProcessNames);
                        target = new CachedTrayTarget(knownHwnd, knownPid, knownClass, knownResolvedUid, proto.CallbackMessage, proto.IsNotifyIconV4);
                        KnownTargetCache[proto.AppKey] = target;
                    }
                    else
                    {
                        target = TryGetValidCachedKnownTarget(proto) ?? await Task.Run(() => FindAndCacheKnownTarget(proto)).ConfigureAwait(false);
                    }

                    if (target == null || target.Hwnd == IntPtr.Zero || !IsWindow(target.Hwnd))
                    {
                        return false;
                    }

                    uint uid = (knownHwnd == IntPtr.Zero || knownHwnd == target.Hwnd)
                        ? (knownUid ?? target.Uid ?? ResolveNotifyIconUidForApp(iconGuid, executablePath, proto.ProcessNames) ?? proto.DefaultUid)
                        : (target.Uid ?? ResolveNotifyIconUidForApp(iconGuid, executablePath, proto.ProcessNames) ?? proto.DefaultUid);

                    if (target.Pid != 0)
                    {
                        _ = AllowSetForegroundWindow(target.Pid);
                    }

                    if (isRightClick)
                    {
                        BeginTrayPopupOverlapSuppression();
                    }

                    int effectiveX = clickX;
                    int effectiveY = clickY;
                    int workAreaTopPx = 64;
                    if (isRightClick && knownHwnd == IntPtr.Zero)
                    {
                        ResolvePopupCoordinatesForTarget(
                            clickX,
                            clickY,
                            proto.IsNotifyIconV4,
                            out effectiveX,
                            out effectiveY,
                            out workAreaTopPx);
                    }

                    bool includeMouseMove = !string.Equals(proto.AppKey, "feishu", StringComparison.OrdinalIgnoreCase);
                    bool posted = PostTrayClickMessages(
                        target.Hwnd,
                        proto.CallbackMessage,
                        uid,
                        effectiveX,
                        effectiveY,
                        proto.IsNotifyIconV4,
                        isRightClick,
                        isDoubleClick,
                        includeMouseMove);

                    DiagnosticLog.Info(
                        $"Relayed custom pinned tray click (known={proto.AppKey}) hwnd=0x{target.Hwnd.ToInt64():X} " +
                        $"pid={target.Pid} uid={uid} msg=0x{proto.CallbackMessage:X} v4={proto.IsNotifyIconV4} " +
                        $"right={isRightClick} dbl={isDoubleClick} pos=({effectiveX},{effectiveY}) posted={posted}.");

                    if (!posted)
                    {
                        KnownTargetCache.TryRemove(proto.AppKey, out _);
                    }
                    else if (isRightClick && string.Equals(proto.AppKey, "wechat", StringComparison.OrdinalIgnoreCase) && target.Pid != 0)
                    {
                        _ = EnsureWeChatPopupAtTopAsync(target.Pid, clickY, workAreaTopPx);
                    }
                    return posted;
                }

                string cacheKey = BuildArbitraryCacheKey(executablePath, processName, displayName, iconGuid);
                CachedTrayTarget? arbitrary = TryResolveArbitraryFastTarget(
                    cacheKey,
                    executablePath,
                    processName,
                    iconGuid,
                    knownUid,
                    knownHwnd,
                    knownCallbackMessage,
                    knownIsNotifyIconV4);

                if (arbitrary == null)
                {
                    arbitrary = await Task.Run(() => FindAndCacheArbitraryTarget(
                        cacheKey,
                        executablePath,
                        processName,
                        iconGuid,
                        knownUid,
                        knownCallbackMessage,
                        knownIsNotifyIconV4)).ConfigureAwait(false);
                }

                if (arbitrary == null || arbitrary.Hwnd == IntPtr.Zero || arbitrary.CallbackMessage == 0 || !IsWindow(arbitrary.Hwnd))
                {
                    return false;
                }

                uint arbitraryUid = (knownHwnd == IntPtr.Zero || knownHwnd == arbitrary.Hwnd)
                    ? (knownUid ?? arbitrary.Uid ?? ResolveArbitraryDefaultUid(arbitrary.ClassName))
                    : (arbitrary.Uid ?? knownUid ?? ResolveArbitraryDefaultUid(arbitrary.ClassName));

                if (arbitrary.Pid != 0)
                {
                    _ = AllowSetForegroundWindow(arbitrary.Pid);
                }

                if (isRightClick)
                {
                    BeginTrayPopupOverlapSuppression();
                }

                int arbX = clickX;
                int arbY = clickY;
                if (isRightClick && knownHwnd == IntPtr.Zero)
                {
                    ResolvePopupCoordinatesForTarget(
                        clickX,
                        clickY,
                        arbitrary.IsNotifyIconV4,
                        out arbX,
                        out arbY,
                        out _);
                }

                bool arbitraryPosted = PostTrayClickMessages(
                    arbitrary.Hwnd,
                    arbitrary.CallbackMessage,
                    arbitraryUid,
                    arbX,
                    arbY,
                    arbitrary.IsNotifyIconV4,
                    isRightClick,
                    isDoubleClick,
                    includeMouseMove: true);

                DiagnosticLog.Info(
                    $"Relayed custom pinned tray click (app={cacheKey}) hwnd=0x{arbitrary.Hwnd.ToInt64():X} " +
                    $"pid={arbitrary.Pid} cls='{arbitrary.ClassName}' uid={arbitraryUid} msg=0x{arbitrary.CallbackMessage:X} " +
                    $"v4={arbitrary.IsNotifyIconV4} right={isRightClick} dbl={isDoubleClick} pos=({clickX},{clickY}) posted={arbitraryPosted}.");

                if (!arbitraryPosted && !string.IsNullOrEmpty(cacheKey))
                {
                    ArbitraryTargetCache.TryRemove(cacheKey, out _);
                }
                return arbitraryPosted;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"Custom tray click relay failed: {ex.Message}");
                return false;
            }
        }

        internal static bool PostTrayRightClickMessages(
            IntPtr trayHwnd,
            uint callbackMsg,
            uint uid,
            int cursorX,
            int cursorY,
            bool isNotifyIconV4)
        {
            return PostTrayRightClickCore(trayHwnd, callbackMsg, uid, cursorX, cursorY, isNotifyIconV4, includeMouseMove: true);
        }

        internal static bool PostTrayRightClickCore(
            IntPtr trayHwnd,
            uint callbackMsg,
            uint uid,
            int cursorX,
            int cursorY,
            bool isNotifyIconV4,
            bool includeMouseMove)
        {
            if (trayHwnd == IntPtr.Zero || callbackMsg == 0)
            {
                return false;
            }

            if (isNotifyIconV4)
            {
                uint xy = (((uint)cursorY & 0xFFFFu) << 16) | ((uint)cursorX & 0xFFFFu);
                var wParam = new UIntPtr(xy);
                uint hiUid = uid << 16;

                bool okMove = includeMouseMove && PostMessage(trayHwnd, callbackMsg, wParam, new IntPtr(unchecked((int)(hiUid | WmMouseMove))));
                bool okDown = PostMessage(trayHwnd, callbackMsg, wParam, new IntPtr(unchecked((int)(hiUid | WmRButtonDown))));
                bool okUp = PostMessage(trayHwnd, callbackMsg, wParam, new IntPtr(unchecked((int)(hiUid | WmRButtonUp))));
                bool okCtx = PostMessage(trayHwnd, callbackMsg, wParam, new IntPtr(unchecked((int)(hiUid | WmContextMenu))));
                return okUp || okCtx || okDown || okMove;
            }
            else
            {
                var wParam = new UIntPtr(uid);
                bool okMove = includeMouseMove && PostMessage(trayHwnd, callbackMsg, wParam, new IntPtr((int)WmMouseMove));
                bool okDown = PostMessage(trayHwnd, callbackMsg, wParam, new IntPtr((int)WmRButtonDown));
                bool okUp = PostMessage(trayHwnd, callbackMsg, wParam, new IntPtr((int)WmRButtonUp));
                return okUp || okDown || okMove;
            }
        }

        internal static bool PostTrayClickMessages(
            IntPtr trayHwnd,
            uint callbackMsg,
            uint uid,
            int cursorX,
            int cursorY,
            bool isNotifyIconV4,
            bool isRightClick,
            bool isDoubleClick,
            bool includeMouseMove = true)
        {
            if (isRightClick)
            {
                return PostTrayRightClickCore(trayHwnd, callbackMsg, uid, cursorX, cursorY, isNotifyIconV4, includeMouseMove);
            }

            if (trayHwnd == IntPtr.Zero || callbackMsg == 0)
            {
                return false;
            }

            if (isNotifyIconV4)
            {
                uint xy = (((uint)cursorY & 0xFFFFu) << 16) | ((uint)cursorX & 0xFFFFu);
                var wParam = new UIntPtr(xy);
                uint hiUid = uid << 16;

                if (isDoubleClick)
                {
                    bool okDbl = PostMessage(trayHwnd, callbackMsg, wParam, new IntPtr(unchecked((int)(hiUid | WmLButtonDblClk))));
                    bool okUp = PostMessage(trayHwnd, callbackMsg, wParam, new IntPtr(unchecked((int)(hiUid | WmLButtonUp))));
                    return okDbl || okUp;
                }

                bool okDown = PostMessage(trayHwnd, callbackMsg, wParam, new IntPtr(unchecked((int)(hiUid | WmLButtonDown))));
                bool okLUp = PostMessage(trayHwnd, callbackMsg, wParam, new IntPtr(unchecked((int)(hiUid | WmLButtonUp))));
                bool okSel = PostMessage(trayHwnd, callbackMsg, wParam, new IntPtr(unchecked((int)(hiUid | NinSelect))));
                return okSel || okLUp || okDown;
            }
            else
            {
                var wParam = new UIntPtr(uid);
                if (isDoubleClick)
                {
                    bool okDbl = PostMessage(trayHwnd, callbackMsg, wParam, new IntPtr((int)WmLButtonDblClk));
                    bool okUp = PostMessage(trayHwnd, callbackMsg, wParam, new IntPtr((int)WmLButtonUp));
                    return okDbl || okUp;
                }

                bool okDown = PostMessage(trayHwnd, callbackMsg, wParam, new IntPtr((int)WmLButtonDown));
                bool okLUp = PostMessage(trayHwnd, callbackMsg, wParam, new IntPtr((int)WmLButtonUp));
                return okLUp || okDown;
            }
        }

        internal static TrayAppProtocol? ResolveAppProtocol(
            string? appId,
            string? displayName,
            string? launchPath,
            IReadOnlyList<string>? extraProcessNames = null)
        {
            string? exeBase = null;
            if (!string.IsNullOrWhiteSpace(launchPath))
            {
                try
                {
                    exeBase = Path.GetFileNameWithoutExtension(launchPath);
                }
                catch
                {
                    exeBase = null;
                }
            }

            string extra = extraProcessNames != null ? string.Join(" ", extraProcessNames) : string.Empty;
            string combined = $"{appId} {displayName} {exeBase} {extra}".ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(combined))
            {
                return null;
            }

            // Note: check wecom/wxwork BEFORE wechat because "wecom" doesn't collide, whereas "wxwork" != "weixin".
            if (combined.Contains("wxwork") || combined.Contains("wecom") || combined.Contains("企业微信"))
            {
                return FindProtocolByKey("wecom");
            }
            if (combined.Contains("weixin") || combined.Contains("wechat") || combined.Contains("微信"))
            {
                return FindProtocolByKey("wechat");
            }
            if (combined.Contains("feishu") || combined.Contains("lark") || combined.Contains("飞书"))
            {
                return FindProtocolByKey("feishu");
            }
            if (combined.Contains("dingtalk") || combined.Contains("dingding") || combined.Contains("钉钉"))
            {
                return FindProtocolByKey("dingtalk");
            }
            if (combined.Contains("wemeet") || combined.Contains("voov") || combined.Contains("腾讯会议"))
            {
                return FindProtocolByKey("wemeet");
            }
            if (combined.Contains("cloudhub") || combined.Contains("云之家") || combined.Contains("yzj"))
            {
                return FindProtocolByKey("cloudhub");
            }
            if (combined.Contains("qq") || combined.Contains("tim"))
            {
                return FindProtocolByKey("qq");
            }

            return null;
        }

        internal static TrayAppProtocol? FindProtocolByKey(string key)
        {
            for (int i = 0; i < KnownApps.Length; i++)
            {
                if (string.Equals(KnownApps[i].AppKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    return KnownApps[i];
                }
            }
            return null;
        }

        internal static bool MatchesClassName(string className, TrayAppProtocol proto)
        {
            if (string.IsNullOrEmpty(className))
            {
                return false;
            }

            bool classMatches = proto.ClassExactMatch
                ? string.Equals(className, proto.ClassPattern, StringComparison.Ordinal)
                : className.Contains(proto.ClassPattern, StringComparison.Ordinal);

            if (!classMatches)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(proto.ClassSecondaryToken) &&
                !className.Contains(proto.ClassSecondaryToken, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        internal static bool MatchesClassAndProcess(string className, string? processName, TrayAppProtocol proto)
        {
            if (!MatchesClassName(className, proto))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(processName))
            {
                return false;
            }

            string normalized = NormalizeProcessName(processName);
            for (int i = 0; i < proto.ProcessNames.Length; i++)
            {
                if (string.Equals(normalized, proto.ProcessNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeProcessName(string name)
        {
            string trimmed = name.Trim();
            return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? trimmed[..^4]
                : trimmed;
        }

        private static void PrewarmPinnedCustomTargets()
        {
            try
            {
                IReadOnlyList<PinnedTrayEntry> entries = PinnedTrayStore.GetSnapshot();
                for (int i = 0; i < entries.Count; i++)
                {
                    PinnedTrayEntry entry = entries[i];
                    Guid? guid = Guid.TryParse(entry.IconGuid, out Guid parsed) ? parsed : null;
                    if (ResolveAppProtocol(null, entry.DisplayName, entry.ExecutablePath,
                            string.IsNullOrWhiteSpace(entry.ProcessName) ? null : new[] { entry.ProcessName }) != null)
                    {
                        continue;
                    }

                    string cacheKey = BuildArbitraryCacheKey(entry.ExecutablePath, entry.ProcessName, entry.DisplayName, guid);
                    if (!string.IsNullOrEmpty(cacheKey) && !ArbitraryTargetCache.ContainsKey(cacheKey))
                    {
                        _ = FindAndCacheArbitraryTarget(
                            cacheKey,
                            entry.ExecutablePath,
                            entry.ProcessName,
                            guid,
                            entry.NotifyIconUid,
                            knownCallbackMessage: null,
                            knownIsNotifyIconV4: null);
                    }
                }
            }
            catch
            {
                // Best-effort cold-start pre-warm.
            }
        }

        private static void ScheduleBackgroundPrewarm()
        {
            if (Interlocked.CompareExchange(ref _prewarmScheduled, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    _ = GetNotifyIconEntries(forceRefresh: true);
                    PrewarmKnownTargets();
                    PrewarmPinnedCustomTargets();

                    // Re-prewarm once after startup settles for slower-initializing tray apps.
                    await Task.Delay(3000).ConfigureAwait(false);
                    _ = GetNotifyIconEntries(forceRefresh: true);
                    PrewarmKnownTargets();
                    PrewarmPinnedCustomTargets();
                }
                catch
                {
                    // Non-fatal background optimization.
                }
            });
        }

        private static void PrewarmKnownTargets()
        {
            var procNameCache = new Dictionary<uint, string?>();
            var classBuf = new StringBuilder(128);

            void InspectWindow(IntPtr h)
            {
                if (!TryReadClassName(h, classBuf, out string cls))
                {
                    return;
                }

                for (int i = 0; i < KnownApps.Length; i++)
                {
                    TrayAppProtocol proto = KnownApps[i];
                    if (KnownTargetCache.ContainsKey(proto.AppKey) || !MatchesClassName(cls, proto))
                    {
                        continue;
                    }

                    _ = GetWindowThreadProcessId(h, out uint pid);
                    string? procName = ResolveProcessNameCached(pid, procNameCache);
                    if (MatchesClassAndProcess(cls, procName, proto))
                    {
                        uint? uid = ResolveNotifyIconUid(proto);
                        KnownTargetCache[proto.AppKey] = new CachedTrayTarget(
                            h,
                            pid,
                            cls,
                            uid,
                            proto.CallbackMessage,
                            proto.IsNotifyIconV4);
                    }
                }
            }

            EnumWindows((h, _) =>
            {
                InspectWindow(h);
                return KnownTargetCache.Count < KnownApps.Length;
            }, IntPtr.Zero);

            if (KnownTargetCache.Count < KnownApps.Length)
            {
                IntPtr child = IntPtr.Zero;
                int safety = 0;
                while (safety++ < 2000 && KnownTargetCache.Count < KnownApps.Length)
                {
                    child = FindWindowEx(HwndMessage, child, null, null);
                    if (child == IntPtr.Zero)
                    {
                        break;
                    }
                    InspectWindow(child);
                }
            }
        }

        private static CachedTrayTarget? TryGetValidCachedKnownTarget(TrayAppProtocol proto)
        {
            if (!KnownTargetCache.TryGetValue(proto.AppKey, out CachedTrayTarget? cached))
            {
                return null;
            }

            if (cached.Hwnd == IntPtr.Zero || !IsWindow(cached.Hwnd))
            {
                KnownTargetCache.TryRemove(proto.AppKey, out _);
                return null;
            }

            _ = GetWindowThreadProcessId(cached.Hwnd, out uint livePid);
            if (livePid == 0 || (cached.Pid != 0 && livePid != cached.Pid))
            {
                KnownTargetCache.TryRemove(proto.AppKey, out _);
                return null;
            }

            var classBuf = new StringBuilder(128);
            if (!TryReadClassName(cached.Hwnd, classBuf, out string liveCls) || !MatchesClassName(liveCls, proto))
            {
                KnownTargetCache.TryRemove(proto.AppKey, out _);
                return null;
            }

            return cached;
        }

        private static bool IsWindowAndMatchesKnownProtocol(
            IntPtr hWnd,
            TrayAppProtocol proto,
            out uint pid,
            out string className)
        {
            pid = 0;
            className = string.Empty;
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
            {
                return false;
            }

            var classBuf = new StringBuilder(128);
            if (!TryReadClassName(hWnd, classBuf, out string cls) || !MatchesClassName(cls, proto))
            {
                return false;
            }

            _ = GetWindowThreadProcessId(hWnd, out pid);
            string? procName = ResolveProcessNameUncached(pid);
            if (!MatchesClassAndProcess(cls, procName, proto))
            {
                return false;
            }

            className = cls;
            return true;
        }

        private static CachedTrayTarget? FindAndCacheKnownTarget(TrayAppProtocol proto)
        {
            var (hwnd, pid, cls) = FindTrayCallbackWindowWithClass(proto);
            if (hwnd == IntPtr.Zero)
            {
                KnownTargetCache.TryRemove(proto.AppKey, out _);
                return null;
            }

            uint? uid = ResolveNotifyIconUid(proto);
            var target = new CachedTrayTarget(hwnd, pid, cls, uid, proto.CallbackMessage, proto.IsNotifyIconV4);
            KnownTargetCache[proto.AppKey] = target;
            return target;
        }

        private static (IntPtr Hwnd, uint Pid) FindTrayCallbackWindow(TrayAppProtocol proto)
        {
            var (hwnd, pid, _) = FindTrayCallbackWindowWithClass(proto);
            return (hwnd, pid);
        }

        private static (IntPtr Hwnd, uint Pid, string ClassName) FindTrayCallbackWindowWithClass(TrayAppProtocol proto)
        {
            IntPtr foundHwnd = IntPtr.Zero;
            uint foundPid = 0;
            string foundClass = string.Empty;
            var procNameCache = new Dictionary<uint, string?>();
            var classBuf = new StringBuilder(128);

            // 1. Enumerate top-level windows (captures hidden overlapped tray message windows like WxTrayIconMessageWindowClass / Lark_StatusTrayWindow).
            EnumWindows((h, _) =>
            {
                if (TryCheckWindowMatch(h, proto, pid => ResolveProcessNameCached(pid, procNameCache), classBuf, out uint pid, out string cls))
                {
                    foundHwnd = h;
                    foundPid = pid;
                    foundClass = cls;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (foundHwnd != IntPtr.Zero)
            {
                return (foundHwnd, foundPid, foundClass);
            }

            // 2. Also scan message-only windows under HWND_MESSAGE (used by apps such as DingTalk CTrayUIMgr_ClassName).
            IntPtr child = IntPtr.Zero;
            int safety = 0;
            while (safety++ < 2000)
            {
                child = FindWindowEx(HwndMessage, child, null, null);
                if (child == IntPtr.Zero)
                {
                    break;
                }

                if (TryCheckWindowMatch(child, proto, pid => ResolveProcessNameCached(pid, procNameCache), classBuf, out uint pid, out string cls))
                {
                    return (child, pid, cls);
                }
            }

            return (IntPtr.Zero, 0, string.Empty);
        }

        private static bool TryCheckWindowMatch(
            IntPtr hWnd,
            TrayAppProtocol proto,
            Func<uint, string?> resolveProcessName,
            StringBuilder classBuf,
            out uint pid,
            out string className)
        {
            pid = 0;
            className = string.Empty;
            if (!TryReadClassName(hWnd, classBuf, out string cls))
            {
                return false;
            }

            // Check class pattern BEFORE querying process name so we avoid ~200 Process.GetProcessById syscalls.
            if (!MatchesClassName(cls, proto))
            {
                return false;
            }

            _ = GetWindowThreadProcessId(hWnd, out pid);
            string? procName = resolveProcessName(pid);
            if (!MatchesClassAndProcess(cls, procName, proto))
            {
                return false;
            }

            className = cls;
            return true;
        }

        private static bool TryReadClassName(IntPtr hWnd, StringBuilder classBuf, out string className)
        {
            classBuf.Clear();
            int len = GetClassName(hWnd, classBuf, classBuf.Capacity);
            if (len <= 0)
            {
                className = string.Empty;
                return false;
            }

            className = classBuf.ToString();
            return true;
        }

        private static string? ResolveProcessNameCached(uint pid, Dictionary<uint, string?> cache)
        {
            if (pid == 0)
            {
                return null;
            }

            if (cache.TryGetValue(pid, out string? cached))
            {
                return cached;
            }

            string? name = ResolveProcessNameUncached(pid);
            cache[pid] = name;
            return name;
        }

        private static string? ResolveProcessNameUncached(uint pid)
        {
            if (pid == 0)
            {
                return null;
            }

            try
            {
                using Process p = Process.GetProcessById((int)pid);
                return p.ProcessName;
            }
            catch
            {
                return null;
            }
        }

        private static string BuildArbitraryCacheKey(
            string? executablePath,
            string? processName,
            string? displayName,
            Guid? iconGuid)
        {
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                return executablePath.Trim();
            }
            if (!string.IsNullOrWhiteSpace(processName))
            {
                return NormalizeProcessName(processName);
            }
            if (iconGuid.HasValue && iconGuid.Value != Guid.Empty)
            {
                return iconGuid.Value.ToString("B");
            }
            return displayName?.Trim() ?? string.Empty;
        }

        private static CachedTrayTarget? TryResolveArbitraryFastTarget(
            string cacheKey,
            string? executablePath,
            string? processName,
            Guid? iconGuid,
            uint? knownUid,
            IntPtr knownHwnd,
            uint? knownCallbackMessage,
            bool? knownIsNotifyIconV4)
        {
            var procCandidates = CollectProcessNameCandidates(processName, executablePath);
            if (knownHwnd != IntPtr.Zero && IsWindow(knownHwnd))
            {
                _ = GetWindowThreadProcessId(knownHwnd, out uint livePid);
                if (DoesPidMatchArbitraryApp(livePid, procCandidates, executablePath))
                {
                    var classBuf = new StringBuilder(128);
                    _ = TryReadClassName(knownHwnd, classBuf, out string cls);
                    var (callbackMsg, isV4) = InferCallbackProtocol(
                        knownHwnd,
                        livePid,
                        cls,
                        executablePath,
                        knownCallbackMessage ?? 0u,
                        knownIsNotifyIconV4);
                    if (callbackMsg != 0)
                    {
                        uint? resolvedUid = knownUid ?? ResolveNotifyIconUidForApp(iconGuid, executablePath, procCandidates);
                        var live = new CachedTrayTarget(knownHwnd, livePid, cls, resolvedUid, callbackMsg, isV4);
                        if (!string.IsNullOrEmpty(cacheKey))
                        {
                            ArbitraryTargetCache[cacheKey] = live;
                        }
                        return live;
                    }
                }
            }

            if (!string.IsNullOrEmpty(cacheKey) && ArbitraryTargetCache.TryGetValue(cacheKey, out CachedTrayTarget? cached))
            {
                if (cached.Hwnd != IntPtr.Zero && IsWindow(cached.Hwnd))
                {
                    _ = GetWindowThreadProcessId(cached.Hwnd, out uint livePid);
                    if (livePid != 0 && (cached.Pid == 0 || livePid == cached.Pid))
                    {
                        uint callbackMsg = (knownCallbackMessage.HasValue && knownCallbackMessage.Value != 0)
                            ? knownCallbackMessage.Value
                            : cached.CallbackMessage;
                        if (callbackMsg != 0)
                        {
                            bool isV4 = knownIsNotifyIconV4 ?? cached.IsNotifyIconV4;
                            uint? uid = knownUid ?? cached.Uid;
                            return cached with { CallbackMessage = callbackMsg, IsNotifyIconV4 = isV4, Uid = uid };
                        }
                    }
                }

                ArbitraryTargetCache.TryRemove(cacheKey, out _);
            }

            return null;
        }

        private static CachedTrayTarget? FindAndCacheArbitraryTarget(
            string cacheKey,
            string? executablePath,
            string? processName,
            Guid? iconGuid,
            uint? knownUid,
            uint? knownCallbackMessage,
            bool? knownIsNotifyIconV4)
        {
            string[] procCandidates = CollectProcessNameCandidates(processName, executablePath);
            if (procCandidates.Length == 0)
            {
                return null;
            }

            var candidatePids = new HashSet<uint>();
            var exactExePids = new HashSet<uint>();
            string? normalizedTargetPath = NormalizeFullPath(executablePath);

            for (int i = 0; i < procCandidates.Length; i++)
            {
                Process[] procs;
                try
                {
                    procs = Process.GetProcessesByName(procCandidates[i]);
                }
                catch
                {
                    continue;
                }

                foreach (Process p in procs)
                {
                    try
                    {
                        uint pid = (uint)p.Id;
                        if (pid == 0)
                        {
                            continue;
                        }

                        if (!string.IsNullOrEmpty(normalizedTargetPath))
                        {
                            string? mainModulePath = TryGetProcessPath(p);
                            if (!string.IsNullOrEmpty(mainModulePath))
                            {
                                if (!string.Equals(mainModulePath, normalizedTargetPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }
                                exactExePids.Add(pid);
                            }
                        }

                        candidatePids.Add(pid);
                    }
                    catch
                    {
                        // Process exited mid-enumeration.
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }

            if (candidatePids.Count == 0)
            {
                return null;
            }

            IntPtr bestHwnd = IntPtr.Zero;
            uint bestPid = 0;
            string bestClass = string.Empty;
            int bestScore = int.MinValue;
            var classBuf = new StringBuilder(128);

            void ScoreCandidate(IntPtr h)
            {
                _ = GetWindowThreadProcessId(h, out uint pid);
                if (pid == 0 || !candidatePids.Contains(pid))
                {
                    return;
                }

                if (!TryReadClassName(h, classBuf, out string cls))
                {
                    return;
                }

                int score = ScoreTrayWindowClass(h, cls, exactExePids.Contains(pid));
                if (score > bestScore)
                {
                    bestScore = score;
                    bestHwnd = h;
                    bestPid = pid;
                    bestClass = cls;
                }
            }

            EnumWindows((h, _) =>
            {
                ScoreCandidate(h);
                return true;
            }, IntPtr.Zero);

            IntPtr child = IntPtr.Zero;
            int safety = 0;
            while (safety++ < 2000)
            {
                child = FindWindowEx(HwndMessage, child, null, null);
                if (child == IntPtr.Zero)
                {
                    break;
                }
                ScoreCandidate(child);
            }

            if (bestHwnd == IntPtr.Zero)
            {
                return null;
            }

            var (callbackMsg, isV4) = InferCallbackProtocol(
                bestHwnd,
                bestPid,
                bestClass,
                executablePath,
                knownCallbackMessage ?? 0u,
                knownIsNotifyIconV4);

            if (callbackMsg == 0)
            {
                return null;
            }

            uint? resolvedUid = knownUid ?? ResolveNotifyIconUidForApp(iconGuid, executablePath, procCandidates);

            var found = new CachedTrayTarget(bestHwnd, bestPid, bestClass, resolvedUid, callbackMsg, isV4);
            if (!string.IsNullOrEmpty(cacheKey))
            {
                ArbitraryTargetCache[cacheKey] = found;
            }
            return found;
        }

        internal static bool IsSystemHelperWindowClass(string cls)
        {
            if (string.IsNullOrWhiteSpace(cls))
            {
                return true;
            }

            return cls.Equals("Default IME", StringComparison.OrdinalIgnoreCase) ||
                   cls.Equals("MSCTFIME UI", StringComparison.OrdinalIgnoreCase) ||
                   cls.StartsWith("GDI+ Hook Window Class", StringComparison.OrdinalIgnoreCase) ||
                   cls.Equals("OleMainThreadWndClass", StringComparison.OrdinalIgnoreCase) ||
                   cls.Equals("CicMarshalWndClass", StringComparison.OrdinalIgnoreCase) ||
                   cls.Equals("tooltips_class32", StringComparison.OrdinalIgnoreCase) ||
                   cls.Equals("IME", StringComparison.OrdinalIgnoreCase);
        }

        private static int ScoreTrayWindowClass(IntPtr hWnd, string cls, bool exactExeMatch)
        {
            if (IsSystemHelperWindowClass(cls))
            {
                return -1000;
            }

            int score = exactExeMatch ? 30 : 0;

            if (string.Equals(cls, "Electron_NotifyIconHostWindow", StringComparison.Ordinal))
            {
                score += 200;
            }
            else if (cls.StartsWith("QTrayIconMessageWindow", StringComparison.Ordinal) ||
                     cls.StartsWith("EVERYTHING_TASKBAR_NOTIFICATION", StringComparison.OrdinalIgnoreCase))
            {
                score += 190;
            }
            else if (cls.Contains("NotifyIcon", StringComparison.OrdinalIgnoreCase) ||
                     cls.Contains("TrayIcon", StringComparison.OrdinalIgnoreCase) ||
                     cls.Contains("StatusTray", StringComparison.OrdinalIgnoreCase) ||
                     cls.Contains("TrayNotify", StringComparison.OrdinalIgnoreCase) ||
                     cls.Contains("TrayUI", StringComparison.OrdinalIgnoreCase) ||
                     cls.Contains("Systray", StringComparison.OrdinalIgnoreCase) ||
                     cls.Contains("QWindowIcon", StringComparison.Ordinal))
            {
                score += 150;
            }

            bool visible = IsWindowVisible(hWnd);
            if (!visible)
            {
                if (cls.StartsWith("WindowsForms10.Window.", StringComparison.Ordinal))
                {
                    score += 90;
                }
                else if (cls.Contains("QWindow", StringComparison.OrdinalIgnoreCase) ||
                         cls.Contains("Qt", StringComparison.OrdinalIgnoreCase))
                {
                    score += 70;
                }
                else
                {
                    score += 40;
                }
            }
            else
            {
                score -= 100;
            }

            return score;
        }

        private static readonly ConcurrentDictionary<string, (uint Msg, bool IsV4)?> PeProtocolCache =
            new(StringComparer.OrdinalIgnoreCase);

        internal static (uint CallbackMessage, bool IsV4) InferCallbackProtocol(
            IntPtr hWnd,
            uint pid,
            string cls,
            string? executablePath,
            uint knownCallback,
            bool? knownIsV4)
        {
            if (knownCallback != 0)
            {
                return (knownCallback, knownIsV4 ?? InferV4FromClass(cls));
            }

            uint clsMsg = InferCallbackMessageFromClass(cls, 0u);
            if (clsMsg != 0)
            {
                return (clsMsg, knownIsV4 ?? InferV4FromClass(cls));
            }

            // Automatic framework-agnostic discovery for any native x64 app via Shell_NotifyIconW/A PE call sites.
            if (pid != 0 || !string.IsNullOrWhiteSpace(executablePath))
            {
                (uint peMsg, bool peV4)? discovered = DiscoverPeNotifyIconProtocol(pid, executablePath);
                if (discovered.HasValue && discovered.Value.peMsg != 0)
                {
                    return (discovered.Value.peMsg, knownIsV4 ?? discovered.Value.peV4);
                }
            }

            return (0u, knownIsV4 ?? false);
        }

        private static bool InferV4FromClass(string cls) =>
            cls.StartsWith("QTrayIconMessageWindow", StringComparison.Ordinal);

        private static uint InferCallbackMessageFromClass(string cls, uint cachedCallback)
        {
            if (cachedCallback != 0)
            {
                return cachedCallback;
            }

            // Electron's Tray::OnWndProc always uses WM_APP + 1 (0x8001), e.g., DeepSeek Harness, QQ, CloudHub.
            if (string.Equals(cls, "Electron_NotifyIconHostWindow", StringComparison.Ordinal))
            {
                return 0x8001;
            }

            // Qt 5 / Qt 6 platform plugin (qwindows.dll) creates QTrayIconMessageWindow with MY_TRAY_ICON_MESSAGE = 0x8065 (WM_APP + 101) & VERSION_4.
            if (cls.StartsWith("QTrayIconMessageWindow", StringComparison.Ordinal))
            {
                return 0x8065;
            }

            // Voidtools Everything uses EVERYTHING_TASKBAR_NOTIFICATION with uCallbackMessage = 0x1400 (WM_USER + 0x1000).
            if (cls.StartsWith("EVERYTHING", StringComparison.OrdinalIgnoreCase))
            {
                return 0x1400;
            }

            if (cls.Contains("QWindowIcon", StringComparison.Ordinal))
            {
                return 0x0401;
            }

            if (cls.StartsWith("WindowsForms10.", StringComparison.Ordinal) ||
                cls.Contains(".NET-BroadcastEventWindow", StringComparison.Ordinal))
            {
                return 0x0800;
            }

            return 0;
        }

        internal static (uint Msg, bool IsV4)? DiscoverPeNotifyIconProtocol(uint pid, string? executablePath)
        {
            var candidateFiles = new List<string>();
            if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
            {
                candidateFiles.Add(executablePath);
            }

            if (pid != 0)
            {
                try
                {
                    using Process p = Process.GetProcessById((int)pid);
                    string? mainPath = TryGetProcessPath(p);
                    if (!string.IsNullOrWhiteSpace(mainPath) && File.Exists(mainPath) &&
                        !candidateFiles.Contains(mainPath, StringComparer.OrdinalIgnoreCase))
                    {
                        candidateFiles.Insert(0, mainPath);
                    }

                    string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    foreach (ProcessModule mod in p.Modules)
                    {
                        string? mp = mod.FileName;
                        if (string.IsNullOrWhiteSpace(mp) ||
                            (!string.IsNullOrEmpty(winDir) && mp.StartsWith(winDir, StringComparison.OrdinalIgnoreCase)) ||
                            !File.Exists(mp) ||
                            candidateFiles.Contains(mp, StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        candidateFiles.Add(mp);
                    }
                }
                catch
                {
                    // Process exited or protected; still scan executablePath if available.
                }
            }

            foreach (string file in candidateFiles)
            {
                (uint Msg, bool IsV4)? res = PeProtocolCache.GetOrAdd(file, ScanPeFileForNotifyIcon);
                if (res.HasValue && res.Value.Msg != 0)
                {
                    DiagnosticLog.Info($"Auto-discovered tray callback from PE '{Path.GetFileName(file)}': msg=0x{res.Value.Msg:X} v4={res.Value.IsV4}.");
                    return res;
                }
            }

            return null;
        }

        internal static (uint Msg, bool IsV4)? ScanPeFileForNotifyIcon(string pePath)
        {
            try
            {
                FileInfo fi = new(pePath);
                if (!fi.Exists || fi.Length < 0x100 || fi.Length > 48L * 1024 * 1024)
                {
                    return null;
                }

                byte[] b = File.ReadAllBytes(pePath);
                if (b[0] != (byte)'M' || b[1] != (byte)'Z')
                {
                    return null;
                }

                int pe = BitConverter.ToInt32(b, 0x3C);
                if (pe < 0 || pe + 24 > b.Length ||
                    b[pe] != (byte)'P' || b[pe + 1] != (byte)'E' || b[pe + 2] != 0 || b[pe + 3] != 0)
                {
                    return null;
                }

                ushort machine = BitConverter.ToUInt16(b, pe + 4);
                if (machine != 0x8664)
                {
                    return null;
                }

                ushort nsec = BitConverter.ToUInt16(b, pe + 6);
                ushort optSize = BitConverter.ToUInt16(b, pe + 20);
                int optOff = pe + 24;
                if (optOff + optSize > b.Length || optSize < 128)
                {
                    return null;
                }

                ulong imageBase = BitConverter.ToUInt64(b, optOff + 24);
                uint numDirs = BitConverter.ToUInt32(b, optOff + 108);
                uint impRva = numDirs > 1 ? BitConverter.ToUInt32(b, optOff + 120) : 0; // DataDirectory[1]
                uint delayImpRva = (numDirs > 13 && optSize >= 224) ? BitConverter.ToUInt32(b, optOff + 216) : 0; // DataDirectory[13]
                if (impRva == 0 && delayImpRva == 0)
                {
                    return null;
                }

                int secOff = optOff + optSize;
                if (secOff + nsec * 40 > b.Length)
                {
                    return null;
                }

                var secs = new (uint Va, uint VSize, uint Ra, uint RSize, uint Chars)[nsec];
                for (int i = 0; i < nsec; i++)
                {
                    int s = secOff + i * 40;
                    secs[i] = (
                        BitConverter.ToUInt32(b, s + 12),
                        BitConverter.ToUInt32(b, s + 8),
                        BitConverter.ToUInt32(b, s + 20),
                        BitConverter.ToUInt32(b, s + 16),
                        BitConverter.ToUInt32(b, s + 36));
                }

                int VaToRa(uint va)
                {
                    for (int i = 0; i < secs.Length; i++)
                    {
                        uint end = secs[i].Va + Math.Max(secs[i].VSize, secs[i].RSize);
                        if (va >= secs[i].Va && va < end)
                        {
                            long ra = secs[i].Ra + (long)(va - secs[i].Va);
                            return ra >= 0 && ra < b.Length ? (int)ra : -1;
                        }
                    }
                    return -1;
                }

                uint NormalizeDelayRva(uint raw, bool isRvaBased)
                {
                    if (isRvaBased || raw == 0)
                    {
                        return raw;
                    }
                    return raw >= imageBase && raw - imageBase <= uint.MaxValue
                        ? (uint)(raw - imageBase)
                        : raw;
                }

                var shellIats = new HashSet<uint>();
                var regMsgWIats = new HashSet<uint>();
                var regMsgAIats = new HashSet<uint>();

                void CollectDllImports(string dllName, uint lookupRva, uint iatRva, bool allowVaMask)
                {
                    int lra = VaToRa(lookupRva != 0 ? lookupRva : iatRva);
                    uint idx = 0;
                    while (lra >= 0 && lra + (int)(idx * 8) + 8 <= b.Length)
                    {
                        ulong thunk = BitConverter.ToUInt64(b, lra + (int)(idx * 8));
                        if (thunk == 0)
                        {
                            break;
                        }

                        if ((thunk & (1UL << 63)) == 0)
                        {
                            uint hintRva = allowVaMask && thunk >= imageBase
                                ? (uint)(thunk - imageBase)
                                : (uint)(thunk & 0x7FFFFFFFUL);
                            int hra = VaToRa(hintRva);
                            if (hra >= 0 && hra + 2 < b.Length)
                            {
                                string fn = ReadAsciiZ(b, hra + 2, 48);
                                uint slotRva = iatRva + idx * 8;
                                if (dllName.Equals("shell32.dll", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (fn == "Shell_NotifyIconW" || fn == "Shell_NotifyIconA")
                                    {
                                        shellIats.Add(slotRva);
                                    }
                                }
                                else if (dllName.Equals("user32.dll", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (fn == "RegisterWindowMessageW")
                                    {
                                        regMsgWIats.Add(slotRva);
                                    }
                                    else if (fn == "RegisterWindowMessageA")
                                    {
                                        regMsgAIats.Add(slotRva);
                                    }
                                }
                            }
                        }
                        idx++;
                    }
                }

                if (impRva != 0)
                {
                    int impRa = VaToRa(impRva);
                    while (impRa >= 0 && impRa + 20 <= b.Length)
                    {
                        uint iltRva = BitConverter.ToUInt32(b, impRa);
                        uint nameRva = BitConverter.ToUInt32(b, impRa + 12);
                        uint iatRva = BitConverter.ToUInt32(b, impRa + 16);
                        if (nameRva == 0)
                        {
                            break;
                        }

                        int dllRa = VaToRa(nameRva);
                        if (dllRa >= 0)
                        {
                            string dllName = ReadAsciiZ(b, dllRa, 40);
                            if (dllName.Equals("shell32.dll", StringComparison.OrdinalIgnoreCase) ||
                                dllName.Equals("user32.dll", StringComparison.OrdinalIgnoreCase))
                            {
                                CollectDllImports(dllName, iltRva, iatRva, allowVaMask: false);
                            }
                        }
                        impRa += 20;
                    }
                }

                if (delayImpRva != 0)
                {
                    int dlRa = VaToRa(delayImpRva);
                    while (dlRa >= 0 && dlRa + 32 <= b.Length)
                    {
                        uint grAttrs = BitConverter.ToUInt32(b, dlRa);
                        bool isRva = (grAttrs & 1) != 0;
                        uint nameRva = NormalizeDelayRva(BitConverter.ToUInt32(b, dlRa + 4), isRva);
                        uint iatRva = NormalizeDelayRva(BitConverter.ToUInt32(b, dlRa + 12), isRva);
                        uint intRva = NormalizeDelayRva(BitConverter.ToUInt32(b, dlRa + 16), isRva);
                        if (nameRva == 0 || (iatRva == 0 && intRva == 0))
                        {
                            break;
                        }

                        int dllRa = VaToRa(nameRva);
                        if (dllRa >= 0)
                        {
                            string dllName = ReadAsciiZ(b, dllRa, 40);
                            if (dllName.Equals("shell32.dll", StringComparison.OrdinalIgnoreCase) ||
                                dllName.Equals("user32.dll", StringComparison.OrdinalIgnoreCase))
                            {
                                CollectDllImports(dllName, intRva, iatRva, allowVaMask: !isRva);
                            }
                        }
                        dlRa += 32;
                    }
                }

                if (shellIats.Count == 0)
                {
                    return null;
                }

                bool MatchesIatCall(int off, uint secVa, uint secRa, HashSet<uint> iats, out int callLen)
                {
                    callLen = 0;
                    if (off + 6 <= b.Length && b[off] == 0xFF && b[off + 1] == 0x15)
                    {
                        int disp = BitConverter.ToInt32(b, off + 2);
                        long targetVa = (long)secVa + (off - secRa) + 6 + disp;
                        if (targetVa >= 0 && targetVa <= uint.MaxValue && iats.Contains((uint)targetVa))
                        {
                            callLen = 6;
                            return true;
                        }
                    }
                    else if (off + 5 <= b.Length && b[off] == 0xE8)
                    {
                        int rel = BitConverter.ToInt32(b, off + 1);
                        long stubVa = (long)secVa + (off - secRa) + 5 + rel;
                        if (stubVa >= 0 && stubVa <= uint.MaxValue)
                        {
                            int stubRa = VaToRa((uint)stubVa);
                            if (stubRa >= 0)
                            {
                                int jmpOff = stubRa;
                                int jmpSize = 6;
                                if (stubRa + 7 <= b.Length && b[stubRa] == 0x48 && b[stubRa + 1] == 0xFF && b[stubRa + 2] == 0x25)
                                {
                                    jmpOff = stubRa + 1;
                                    jmpSize = 7;
                                }
                                if (jmpOff + 6 <= b.Length && b[jmpOff] == 0xFF && b[jmpOff + 1] == 0x25)
                                {
                                    int disp = BitConverter.ToInt32(b, jmpOff + 2);
                                    long targetVa = stubVa + jmpSize + disp;
                                    if (targetVa >= 0 && targetVa <= uint.MaxValue && iats.Contains((uint)targetVa))
                                    {
                                        callLen = 5;
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                    return false;
                }

                // Resolve global uCallbackMessage variables populated via RegisterWindowMessageW/A or static stores.
                var globalMsgValues = new Dictionary<uint, uint>();
                foreach (var sec in secs)
                {
                    if ((sec.Chars & 0x20000000u) == 0 || sec.RSize < 16 || sec.Ra + sec.RSize > b.Length)
                    {
                        continue;
                    }

                    int r0 = (int)sec.Ra;
                    int rEnd = r0 + (int)sec.RSize - 10;
                    for (int off = r0; off <= rEnd; off++)
                    {
                        if (b[off] == 0xC7 && b[off + 1] == 0x05)
                        {
                            uint gVa = (uint)((long)sec.Va + (off - r0) + 10 + BitConverter.ToInt32(b, off + 2));
                            uint imm = BitConverter.ToUInt32(b, off + 6);
                            if (imm >= 0x0400 && imm <= 0xBFFF && !globalMsgValues.ContainsKey(gVa))
                            {
                                globalMsgValues[gVa] = imm;
                            }
                            continue;
                        }

                        int callLen = 0;
                        bool isW = regMsgWIats.Count > 0 && MatchesIatCall(off, sec.Va, sec.Ra, regMsgWIats, out callLen);
                        if (!isW && !(regMsgAIats.Count > 0 && MatchesIatCall(off, sec.Va, sec.Ra, regMsgAIats, out callLen)))
                        {
                            continue;
                        }

                        string? msgName = null;
                        int lookStart = Math.Max(r0, off - 64);
                        for (int k = off - 7; k >= lookStart; k--)
                        {
                            if (b[k] == 0x48 && b[k + 1] == 0x8D && b[k + 2] == 0x0D)
                            {
                                long strVa = (long)sec.Va + (k - r0) + 7 + BitConverter.ToInt32(b, k + 3);
                                if (strVa >= 0 && strVa <= uint.MaxValue)
                                {
                                    int strRa = VaToRa((uint)strVa);
                                    if (strRa >= 0)
                                    {
                                        msgName = isW ? ReadUtf16Z(b, strRa, 128) : ReadAsciiZ(b, strRa, 128);
                                    }
                                }
                                break;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(msgName))
                        {
                            int storeEnd = Math.Min(r0 + (int)sec.RSize - 6, off + callLen + 36);
                            for (int k = off + callLen; k <= storeEnd; k++)
                            {
                                if (b[k] == 0x89 && b[k + 1] == 0x05)
                                {
                                    long gVa = (long)sec.Va + (k - r0) + 6 + BitConverter.ToInt32(b, k + 2);
                                    if (gVa >= 0 && gVa <= uint.MaxValue)
                                    {
                                        uint regMsgId = isW ? RegisterWindowMessageW(msgName) : RegisterWindowMessageA(msgName);
                                        if (regMsgId >= 0xC000 && regMsgId <= 0xFFFF)
                                        {
                                            globalMsgValues[(uint)gVa] = regMsgId;
                                        }
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }

                var msgCounts = new Dictionary<uint, int>();
                bool detectedV4 = false;

                foreach (var sec in secs)
                {
                    if ((sec.Chars & 0x20000000u) == 0 || sec.RSize < 16 || sec.Ra + sec.RSize > b.Length)
                    {
                        continue;
                    }

                    int r0 = (int)sec.Ra;
                    int rEnd = r0 + (int)sec.RSize - 6;
                    for (int off = r0; off <= rEnd; off++)
                    {
                        if (!MatchesIatCall(off, sec.Va, sec.Ra, shellIats, out _))
                        {
                            continue;
                        }

                        // Locate lea rdx, [rsp+base] or [rbp+base] immediately passing &nid (2nd x64 arg)
                        int wStart = Math.Max(r0, off - 220);
                        int foundReg = 0; // 1=rsp, 2=rbp
                        int foundBase = 0;
                        for (int k = wStart; k <= off - 5; k++)
                        {
                            if (b[k] == 0x48 && b[k + 1] == 0x8D)
                            {
                                if (b[k + 2] == 0x54 && b[k + 3] == 0x24)
                                {
                                    foundReg = 1; foundBase = b[k + 4];
                                }
                                else if (b[k + 2] == 0x94 && b[k + 3] == 0x24 && k + 8 <= off)
                                {
                                    foundReg = 1; foundBase = BitConverter.ToInt32(b, k + 4);
                                }
                                else if (b[k + 2] == 0x55)
                                {
                                    foundReg = 2; foundBase = (sbyte)b[k + 3];
                                }
                                else if (b[k + 2] == 0x95 && k + 7 <= off)
                                {
                                    foundReg = 2; foundBase = BitConverter.ToInt32(b, k + 3);
                                }
                            }
                        }

                        if (foundReg == 0)
                        {
                            continue;
                        }

                        int cbOff = foundBase + 0x18;  // NOTIFYICONDATAW.uCallbackMessage
                        int verOff = foundBase + 0x330; // NOTIFYICONDATAW.uVersion
                        int fStart = Math.Max(r0, off - 700);
                        int fEnd = Math.Min((int)(sec.Ra + sec.RSize) - 10, off + 180);
                        var regMsg = new Dictionary<int, uint>();

                        for (int k = fStart; k <= fEnd; k++)
                        {
                            if (b[k] == 0xC7)
                            {
                                int d = int.MinValue;
                                uint imm = 0;
                                int step = 0;
                                if (foundReg == 1 && b[k + 1] == 0x44 && b[k + 2] == 0x24 && k + 8 <= fEnd + 10)
                                {
                                    d = b[k + 3];
                                    imm = BitConverter.ToUInt32(b, k + 4);
                                    step = 7;
                                }
                                else if (foundReg == 1 && b[k + 1] == 0x84 && b[k + 2] == 0x24 && k + 11 <= fEnd + 10)
                                {
                                    d = BitConverter.ToInt32(b, k + 3);
                                    imm = BitConverter.ToUInt32(b, k + 7);
                                    step = 10;
                                }
                                else if (foundReg == 2 && b[k + 1] == 0x45 && k + 7 <= fEnd + 10)
                                {
                                    d = (sbyte)b[k + 2];
                                    imm = BitConverter.ToUInt32(b, k + 3);
                                    step = 6;
                                }
                                else if (foundReg == 2 && b[k + 1] == 0x85 && k + 10 <= fEnd + 10)
                                {
                                    d = BitConverter.ToInt32(b, k + 2);
                                    imm = BitConverter.ToUInt32(b, k + 6);
                                    step = 9;
                                }

                                if (d == cbOff && imm >= 0x0400 && imm <= 0xBFFF)
                                {
                                    msgCounts[imm] = msgCounts.TryGetValue(imm, out int c) ? c + 1 : 1;
                                }
                                else if (d == verOff && imm == 4)
                                {
                                    detectedV4 = true;
                                }

                                if (step > 0)
                                {
                                    k += step;
                                }
                                continue;
                            }

                            if (b[k] >= 0xB8 && b[k] <= 0xBF && k + 5 <= fEnd + 10 &&
                                (k == fStart || (b[k - 1] & 0xF0) != 0x40))
                            {
                                int reg = b[k] - 0xB8;
                                uint imm = BitConverter.ToUInt32(b, k + 1);
                                if (imm >= 0x0400 && imm <= 0xBFFF)
                                {
                                    regMsg[reg] = imm;
                                }
                                else
                                {
                                    regMsg.Remove(reg);
                                }
                                k += 4;
                                continue;
                            }

                            if (b[k] == 0x8B && (b[k + 1] & 0xC7) == 0x05 && k + 6 <= fEnd + 10 &&
                                (k == fStart || (b[k - 1] & 0xFB) != 0x40))
                            {
                                int reg = (b[k + 1] >> 3) & 7;
                                long gVa = (long)sec.Va + (k - r0) + 6 + BitConverter.ToInt32(b, k + 2);
                                if (gVa >= 0 && gVa <= uint.MaxValue && globalMsgValues.TryGetValue((uint)gVa, out uint gMsg))
                                {
                                    regMsg[reg] = gMsg;
                                }
                                else
                                {
                                    regMsg.Remove(reg);
                                }
                                k += 5;
                                continue;
                            }

                            if (b[k] == 0x89 && (k == fStart || (b[k - 1] & 0xFB) != 0x40))
                            {
                                int reg = (b[k + 1] >> 3) & 7;
                                int modrm = b[k + 1] & 0xC7;
                                int d = int.MinValue;
                                int step = 0;
                                if (foundReg == 1 && modrm == 0x44 && b[k + 2] == 0x24 && k + 4 <= fEnd + 10)
                                {
                                    d = b[k + 3];
                                    step = 3;
                                }
                                else if (foundReg == 1 && modrm == 0x84 && b[k + 2] == 0x24 && k + 7 <= fEnd + 10)
                                {
                                    d = BitConverter.ToInt32(b, k + 3);
                                    step = 6;
                                }
                                else if (foundReg == 2 && modrm == 0x45 && k + 3 <= fEnd + 10)
                                {
                                    d = (sbyte)b[k + 2];
                                    step = 2;
                                }
                                else if (foundReg == 2 && modrm == 0x85 && k + 6 <= fEnd + 10)
                                {
                                    d = BitConverter.ToInt32(b, k + 2);
                                    step = 5;
                                }

                                if (d == cbOff && regMsg.TryGetValue(reg, out uint rMsg) &&
                                    rMsg >= 0x0400 && rMsg <= 0xFFFF)
                                {
                                    msgCounts[rMsg] = msgCounts.TryGetValue(rMsg, out int c) ? c + 1 : 1;
                                }

                                if (step > 0)
                                {
                                    k += step;
                                }
                            }
                        }
                    }
                }

                if (msgCounts.Count == 0)
                {
                    return null;
                }

                uint bestMsg = msgCounts.OrderByDescending(kv => kv.Value).First().Key;
                return (bestMsg, detectedV4);
            }
            catch
            {
                return null;
            }
        }

        private static string ReadUtf16Z(byte[] b, int offset, int maxChars)
        {
            int end = Math.Min(b.Length - 1, offset + maxChars * 2);
            int bytesLen = 0;
            while (offset + bytesLen + 1 < end && (b[offset + bytesLen] != 0 || b[offset + bytesLen + 1] != 0))
            {
                bytesLen += 2;
            }
            return Encoding.Unicode.GetString(b, offset, bytesLen);
        }

        private static string ReadAsciiZ(byte[] b, int offset, int maxLen)
        {
            int end = Math.Min(b.Length, offset + maxLen);
            int len = 0;
            while (offset + len < end && b[offset + len] != 0)
            {
                len++;
            }
            return Encoding.ASCII.GetString(b, offset, len);
        }

        private static uint ResolveArbitraryDefaultUid(string cls)
        {
            return string.Equals(cls, "Electron_NotifyIconHostWindow", StringComparison.Ordinal) ? 3u : 0u;
        }

        private static string[] CollectProcessNameCandidates(string? processName, string? executablePath)
        {
            var list = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(processName))
            {
                string norm = NormalizeProcessName(processName);
                if (!string.IsNullOrEmpty(norm))
                {
                    list.Add(norm);
                }
            }

            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                try
                {
                    string exeBase = Path.GetFileNameWithoutExtension(executablePath.Trim());
                    if (!string.IsNullOrEmpty(exeBase) &&
                        !list.Contains(exeBase, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(exeBase);
                    }
                }
                catch
                {
                    // Ignore malformed path strings.
                }
            }

            return list.ToArray();
        }

        private static bool DoesPidMatchArbitraryApp(uint pid, string[] procCandidates, string? executablePath)
        {
            if (pid == 0)
            {
                return false;
            }

            if (procCandidates.Length == 0 && string.IsNullOrWhiteSpace(executablePath))
            {
                return true;
            }

            try
            {
                using Process p = Process.GetProcessById((int)pid);
                string normProc = NormalizeProcessName(p.ProcessName);
                for (int i = 0; i < procCandidates.Length; i++)
                {
                    if (string.Equals(normProc, procCandidates[i], StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                string? normalizedTargetPath = NormalizeFullPath(executablePath);
                if (!string.IsNullOrEmpty(normalizedTargetPath))
                {
                    string? actualPath = TryGetProcessPath(p);
                    if (!string.IsNullOrEmpty(actualPath) &&
                        string.Equals(actualPath, normalizedTargetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static string? NormalizeFullPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch
            {
                return path.Trim();
            }
        }

        private static string? TryGetProcessPath(Process p)
        {
            try
            {
                string? fn = p.MainModule?.FileName;
                return NormalizeFullPath(fn);
            }
            catch
            {
                return null;
            }
        }

        private static uint? ResolveNotifyIconUid(TrayAppProtocol proto)
        {
            return ResolveNotifyIconUidForApp(iconGuid: null, executablePath: null, proto.ProcessNames);
        }

        private static uint? ResolveNotifyIconUidForApp(
            Guid? iconGuid,
            string? executablePath,
            params string[] processNames)
        {
            IReadOnlyList<NotifyIconSettingEntry> entries = GetNotifyIconEntries(forceRefresh: false);
            uint? match = MatchNotifyIconEntry(entries, iconGuid, executablePath, processNames);
            if (match.HasValue)
            {
                return match;
            }

            // Refresh once on cache miss in case a newly launched app registered its icon after warm-up.
            entries = GetNotifyIconEntries(forceRefresh: true);
            return MatchNotifyIconEntry(entries, iconGuid, executablePath, processNames);
        }

        private static uint? MatchNotifyIconEntry(
            IReadOnlyList<NotifyIconSettingEntry> entries,
            Guid? iconGuid,
            string? executablePath,
            string[] processNames)
        {
            if (entries.Count == 0)
            {
                return null;
            }

            if (iconGuid.HasValue && iconGuid.Value != Guid.Empty)
            {
                string guidBraced = iconGuid.Value.ToString("B");
                string guidDigits = iconGuid.Value.ToString("D");
                for (int i = 0; i < entries.Count; i++)
                {
                    string sub = entries[i].SubKeyName;
                    if (string.Equals(sub, guidBraced, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(sub, guidDigits, StringComparison.OrdinalIgnoreCase))
                    {
                        return entries[i].Uid;
                    }
                }
            }

            string? normalizedPath = NormalizeFullPath(executablePath);
            if (!string.IsNullOrEmpty(normalizedPath))
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    string? entryPath = NormalizeFullPath(entries[i].ExecutablePath);
                    if (!string.IsNullOrEmpty(entryPath) &&
                        string.Equals(entryPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return entries[i].Uid;
                    }
                }
            }

            for (int p = 0; p < processNames.Length; p++)
            {
                string target = NormalizeProcessName(processNames[p]);
                for (int i = 0; i < entries.Count; i++)
                {
                    if (string.Equals(entries[i].FileNameWithoutExtension, target, StringComparison.OrdinalIgnoreCase))
                    {
                        return entries[i].Uid;
                    }
                }
            }

            return null;
        }

        private static IReadOnlyList<NotifyIconSettingEntry> GetNotifyIconEntries(bool forceRefresh)
        {
            long nowUtc = DateTime.UtcNow.Ticks;
            lock (RegistryCacheSync)
            {
                if (!forceRefresh &&
                    _notifyIconEntriesCache != null &&
                    (nowUtc - _notifyIconEntriesCacheTicksUtc) < NotifyIconCacheTtlTicks)
                {
                    return _notifyIconEntriesCache;
                }

                var list = new List<NotifyIconSettingEntry>();
                try
                {
                    using RegistryKey? root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
                    if (root != null)
                    {
                        foreach (string subName in root.GetSubKeyNames())
                        {
                            using RegistryKey? sub = root.OpenSubKey(subName);
                            if (sub == null)
                            {
                                continue;
                            }

                            string? exePath = sub.GetValue("ExecutablePath") as string;
                            if (string.IsNullOrWhiteSpace(exePath))
                            {
                                continue;
                            }

                            object? uidVal = sub.GetValue("UID");
                            uint uid;
                            if (uidVal is int intUid && intUid >= 0)
                            {
                                uid = (uint)intUid;
                            }
                            else if (uidVal is long longUid && longUid >= 0 && longUid <= uint.MaxValue)
                            {
                                uid = (uint)longUid;
                            }
                            else
                            {
                                continue;
                            }

                            string fileBase;
                            try
                            {
                                fileBase = Path.GetFileNameWithoutExtension(exePath);
                            }
                            catch
                            {
                                continue;
                            }

                            list.Add(new NotifyIconSettingEntry(subName, exePath, fileBase, uid));
                        }
                    }
                }
                catch
                {
                    // Non-fatal: fall back to static UID verified from Honor's TrayActivation tables.
                }

                _notifyIconEntriesCache = list;
                _notifyIconEntriesCacheTicksUtc = nowUtc;
                return list;
            }
        }

        private void CloseTopBarPopups()
        {
            if (_closePopupsExceptMethod != null && _popupSurfaceNone != null)
            {
                try
                {
                    _closePopupsExceptMethod.Invoke(_topBarWindow, new[] { _popupSurfaceNone });
                }
                catch
                {
                    // Non-fatal.
                }
            }
        }

        private void FallbackToHonorRemoveMenu(Button btn)
        {
            if (_disposed || _topBarWindow.Dispatcher.HasShutdownStarted)
            {
                return;
            }

            _topBarWindow.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                if (_disposed || !btn.IsLoaded)
                {
                    return;
                }

                _skipNextRelay = true;
                try
                {
                    var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                    {
                        RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent,
                        Source = btn
                    };
                    btn.RaiseEvent(args);
                }
                finally
                {
                    _skipNextRelay = false;
                }
            }));
        }

        internal static (string? AppId, string? DisplayName, string? LaunchPath, IReadOnlyList<string>? ProcessNames) ReadNotificationItemMetadata(object item)
        {
            Type t = item.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            string? appId = t.GetProperty("AppId", flags)?.GetValue(item) as string;
            string? displayName = t.GetProperty("DisplayName", flags)?.GetValue(item) as string;
            string? launchPath = t.GetProperty("LaunchPath", flags)?.GetValue(item) as string;
            IReadOnlyList<string>? processNames = null;

            object? appDef = t.GetProperty("App", flags)?.GetValue(item);
            if (appDef != null)
            {
                Type at = appDef.GetType();
                appId ??= at.GetProperty("Id", flags)?.GetValue(appDef) as string;
                displayName ??= at.GetProperty("DisplayName", flags)?.GetValue(appDef) as string;
                processNames = at.GetProperty("ProcessNames", flags)?.GetValue(appDef) as IReadOnlyList<string>;
            }

            return (appId, displayName, launchPath, processNames);
        }

        internal static Button? FindNotificationButton(DependencyObject? start)
        {
            DependencyObject? cur = start;
            while (cur != null)
            {
                if (cur is Button btn &&
                    (btn.Tag != null ||
                     (btn.DataContext != null &&
                      btn.DataContext.GetType().Name.Contains("StatusNotification", StringComparison.Ordinal))))
                {
                    return btn;
                }
                cur = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur);
            }
            return null;
        }

        private void HookWorkspaceOverlapGuard()
        {
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

                FieldInfo? hostField = _topBarWindow.GetType().GetField("_workspaceReservationHost", flags);
                object? host = hostField?.GetValue(_topBarWindow);
                if (host == null)
                {
                    return;
                }

                Type hostType = host.GetType();
                _workspaceReservationHost = host;
                _visualVisibleField = hostType.GetField("_visualVisible", flags);
                _overlapField = hostType.GetField("_overlap", flags);

                if (_visualVisibleField?.GetValue(host) is Func<bool> origVis)
                {
                    _origVisualVisible = origVis;
                    Func<bool> wrappedVis = () =>
                    {
                        EnsureSubscriptionOverlapWrapped();
                        if (ShouldSuppressWorkspaceOverlapPulse())
                        {
                            CancelPendingFixedBarOverlap();
                            return false;
                        }
                        return origVis();
                    };
                    _visualVisibleField.SetValue(host, wrappedVis);
                }

                EnsureSubscriptionOverlapWrapped();

                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? coordType = asm.GetType("MagicTaskbarWpf.FixedBarOverlapCoordinator", throwOnError: false);
                    if (coordType != null)
                    {
                        _sharedOverlapCoordinator = coordType.GetProperty("Shared", staticFlags)?.GetValue(null);
                        _cancelPendingOverlapMethod = coordType.GetMethod("CancelPending", flags);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"StatusNotificationTrayRelay HookWorkspaceOverlapGuard error: {ex.Message}");
            }
        }

        private void EnsureSubscriptionOverlapWrapped()
        {
            try
            {
                if (_workspaceReservationHost == null || _overlapField == null)
                {
                    return;
                }

                object? sub = _overlapField.GetValue(_workspaceReservationHost);
                if (sub == null || ReferenceEquals(sub, _hookedOverlapSubscription))
                {
                    return;
                }

                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                Type subType = sub.GetType();
                _subVisibleField = subType.GetField("Visible", flags);
                _subPublishField = subType.GetField("Publish", flags);

                if (_subVisibleField?.GetValue(sub) is Func<bool> origSubVis)
                {
                    _origSubVisible = origSubVis;
                    _subVisibleField.SetValue(sub, new Func<bool>(() =>
                    {
                        if (ShouldSuppressWorkspaceOverlapPulse())
                        {
                            CancelPendingFixedBarOverlap();
                            return false;
                        }
                        return origSubVis();
                    }));
                }

                if (_subPublishField?.GetValue(sub) is Func<bool> origSubPub)
                {
                    _origSubPublish = origSubPub;
                    _subPublishField.SetValue(sub, new Func<bool>(() =>
                    {
                        if (ShouldSuppressWorkspaceOverlapPulse())
                        {
                            CancelPendingFixedBarOverlap();
                            return false;
                        }
                        return origSubPub();
                    }));
                }

                _hookedOverlapSubscription = sub;
            }
            catch
            {
                // Non-fatal.
            }
        }

        private void UnhookWorkspaceOverlapGuard()
        {
            try
            {
                if (_workspaceReservationHost != null && _visualVisibleField != null && _origVisualVisible != null)
                {
                    _visualVisibleField.SetValue(_workspaceReservationHost, _origVisualVisible);
                }

                if (_hookedOverlapSubscription != null)
                {
                    if (_subVisibleField != null && _origSubVisible != null)
                    {
                        _subVisibleField.SetValue(_hookedOverlapSubscription, _origSubVisible);
                    }
                    if (_subPublishField != null && _origSubPublish != null)
                    {
                        _subPublishField.SetValue(_hookedOverlapSubscription, _origSubPublish);
                    }
                }
            }
            catch
            {
                // Non-fatal.
            }
        }

        private static void BeginTrayPopupOverlapSuppression()
        {
            Volatile.Write(ref _suppressWorkspaceOverlapUntilTicksUtc, DateTime.UtcNow.AddSeconds(6).Ticks);
            CancelPendingFixedBarOverlap();
        }

        private static void CancelPendingFixedBarOverlap()
        {
            try
            {
                if (_sharedOverlapCoordinator != null && _cancelPendingOverlapMethod != null)
                {
                    _cancelPendingOverlapMethod.Invoke(_sharedOverlapCoordinator, null);
                }
            }
            catch
            {
                // Non-fatal.
            }
        }

        private static bool ShouldSuppressWorkspaceOverlapPulse()
        {
            if (DateTime.UtcNow.Ticks < Volatile.Read(ref _suppressWorkspaceOverlapUntilTicksUtc))
            {
                return true;
            }

            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || IsZoomed(fg))
            {
                return false;
            }

            // Never let TopBarWorkspaceReservationHost.RepublishOverlap pulse SHAppBarMessage(ABM_SETPOS)
            // when a tray context menu or small popup window is active, as Windows will push the open menu downward.
            if (GetWindowRect(fg, out RECT r))
            {
                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                if (w > 0 && h > 0 && (w < 960 || h < 760))
                {
                    return true;
                }
            }

            var sb = new StringBuilder(128);
            int len = GetClassName(fg, sb, sb.Capacity);
            if (len > 0)
            {
                string cls = sb.ToString();
                if (string.Equals(cls, "#32768", StringComparison.Ordinal) ||
                    cls.Contains("Popup", StringComparison.OrdinalIgnoreCase) ||
                    ((cls.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal) ||
                      cls.StartsWith("Qt", StringComparison.Ordinal)) &&
                     GetWindowTextLengthW(fg) == 0))
                {
                    return true;
                }
            }

            return false;
        }

        private static void ResolvePopupCoordinatesForTarget(
            int clickX,
            int clickY,
            bool isNotifyIconV4,
            out int effectiveX,
            out int effectiveY,
            out int workAreaTopPx)
        {
            effectiveX = clickX;
            effectiveY = clickY;
            workAreaTopPx = 64;

            try
            {
                IntPtr hMon = MonitorFromPoint(new POINT { X = clickX, Y = clickY }, MonitorDefaultToNearest);
                if (hMon != IntPtr.Zero)
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfoW(hMon, ref mi) &&
                        mi.rcWork.Right > mi.rcWork.Left + 16 &&
                        mi.rcWork.Bottom > mi.rcWork.Top + 16)
                    {
                        workAreaTopPx = mi.rcWork.Top;
                        if (isNotifyIconV4)
                        {
                            // Weixin.dll (0x01D52D10) checks workArea.contains(pos). Because the fixed TopBar reserves
                            // 0..rcWork.Top as an AppBar, clicking inside the TopBar produces clickY < rcWork.Top,
                            // causing workArea.contains(pos) to return false and Weixin.dll (0x01D52F88) to jump pos.y
                            // to workArea.bottom. Clamping into rcWork makes workArea.contains(pos) true so Weixin.dll
                            // (0x0136B8A0) clamps the menu's visible top edge flush to workArea.top below the icon.
                            effectiveX = Math.Clamp(clickX, mi.rcWork.Left + 4, mi.rcWork.Right - 4);
                            effectiveY = Math.Clamp(clickY, mi.rcWork.Top + 4, mi.rcWork.Bottom - 4);
                        }
                    }
                }
            }
            catch
            {
                // Fallback to raw coordinates.
            }
        }

        private static async Task EnsureWeChatPopupAtTopAsync(uint wechatPid, int clickY, int workAreaTopPx)
        {
            try
            {
                int maxAllowedTopPx = Math.Max(workAreaTopPx, clickY) + 160;
                int targetTopPx = Math.Max(4, workAreaTopPx - 24);
                var sb = new StringBuilder(128);

                for (int attempt = 0; attempt < 18; attempt++)
                {
                    await Task.Delay(10).ConfigureAwait(false);

                    IntPtr foundPopup = IntPtr.Zero;
                    RECT foundRect = default;

                    EnumWindows((hwnd, _) =>
                    {
                        if (!IsWindowVisible(hwnd))
                        {
                            return true;
                        }

                        GetWindowThreadProcessId(hwnd, out uint pid);
                        if (pid != wechatPid)
                        {
                            return true;
                        }

                        sb.Clear();
                        if (GetClassName(hwnd, sb, sb.Capacity) <= 0)
                        {
                            return true;
                        }

                        string cls = sb.ToString();
                        if (cls.Contains("QWindowPopup", StringComparison.OrdinalIgnoreCase) &&
                            GetWindowRect(hwnd, out RECT r) &&
                            r.Right > r.Left && r.Bottom > r.Top)
                        {
                            foundPopup = hwnd;
                            foundRect = r;
                            return false;
                        }

                        return true;
                    }, IntPtr.Zero);

                    if (foundPopup != IntPtr.Zero)
                    {
                        if (foundRect.Top > maxAllowedTopPx)
                        {
                            _ = SetWindowPos(
                                foundPopup,
                                IntPtr.Zero,
                                foundRect.Left,
                                targetTopPx,
                                0,
                                0,
                                SwpNoSize | SwpNoZOrder | SwpNoActivate);
                            DiagnosticLog.Info(
                                $"Repositioned WeChat tray popup hwnd=0x{foundPopup.ToInt64():X} from Y={foundRect.Top} to Y={targetTopPx}.");
                        }
                        return;
                    }
                }
            }
            catch
            {
                // Non-fatal.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposedFlag, 1) != 0)
            {
                return;
            }

            Interlocked.Increment(ref _relayGeneration);
            _itemsControl?.RemoveHandler(UIElement.PreviewMouseRightButtonUpEvent, _previewRightUpHandler);
            UnhookWorkspaceOverlapGuard();

            lock (RegistrationSync)
            {
                if (_activeInstanceCount > 0)
                {
                    _activeInstanceCount--;
                }

                if (_activeInstanceCount == 0)
                {
                    HiddenTrayPinEnhancer.RegisterCustomTrayRelay(null);
                }
            }
        }

        private const uint MonitorDefaultToNearest = 0x00000002;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLengthW(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(uint dwProcessId);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessageW(string lpString);

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern uint RegisterWindowMessageA(string lpString);
    }
}

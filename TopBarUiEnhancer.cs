using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TopBarAdaptiveColor
{
    /// <summary>
    /// Handles modular TopBar UI enhancements outside adaptive color sampling:
    /// YOYO Claw button visibility and click-mode / zero-spawn IPC responsiveness on
    /// ControlCenterButton and NotificationButton.
    /// </summary>
    internal sealed class TopBarUiEnhancer : IDisposable
    {
        private const uint WmSecondOpen = 0x0800; // UICommonTools.WM_SECONDOPEN (WM_USER * 2) in MessageCenterUI.exe
        private const uint WmControlCenterTray = 0x0401; // WM_USER + 1 tray callback on HonorControlCenterWndTray
        private const uint WmActivate = 0x0006; // WA_INACTIVE (wParam=0) deactivates external flyouts to trigger native Hide
        private long _lastNotificationOpenedTick;
        private long _notifToggleGeneration;
        private const int ControlCenterTrayIconId = 100;
        private const int WmLButtonUp = 0x0202;
        private const int PopupSurfaceControlCenter = 6;
        private const int PopupSurfaceNotification = 8;
        private const string ControlCenterTrayClassName = "HonorControlCenterWndTray";
        private const string ControlCenterWindowTitle = "AiDesktopMainWindow";
        private const string ControlCenterExePath = @"C:\Program Files\HONOR\NotifControlUI\ControlCenterTrayUI.exe";
        private const string MessageCenterWindowTitle = "MessageCenterUI";
        private const string MessageCenterExePath = @"C:\Program Files\HONOR\PCManager\MessageCenterUI.exe";

        private static readonly object LifecycleSync = new();
        private static readonly HashSet<TopBarUiEnhancer> ActiveInstances = new();
        private static bool _everAttachedToTopBar;
        private static int _messageCenterPrewarmScheduled;

        private readonly Window _window;
        private readonly Button? _yoyoButton;
        private readonly Button? _controlCenterButton;
        private readonly Button? _notificationButton;
        private readonly FrameworkElement? _notificationIcon;
        private readonly ItemsControl? _statusItemsControl;
        private readonly DependencyPropertyDescriptor? _yoyoVisibilityDescriptor;
        private readonly DependencyPropertyDescriptor? _notifSourceDescriptor;
        private readonly DependencyPropertyDescriptor? _notifBgDescriptor;
        private readonly DispatcherTimer? _notifAlertTimer;
        private readonly Dictionary<object, RotateTransform> _appShakeRotates = new();
        private RotateTransform? _notifShakeRotate;
        private bool _notifAlertActive;
        private bool _isUpdatingNotifVisual;
        private long _lastNotifShakeTick;
        private long _lastAppShakeTick;
        private const string NotificationAlertHex = "#FF6A2B";
        private readonly ClickMode _origControlCenterClickMode = ClickMode.Release;
        private readonly ClickMode _origNotificationClickMode = ClickMode.Release;
        private readonly RoutedEventHandler? _origControlCenterClickHandler;
        private readonly RoutedEventHandler _fastControlCenterClickHandler;
        private readonly RoutedEventHandler? _origNotificationClickHandler;
        private readonly RoutedEventHandler _fastNotificationClickHandler;
        private readonly FieldInfo? _hasNotificationField;
        private readonly FieldInfo? _systemServiceField;
        private readonly FieldInfo? _startSharedServicesField;
        private readonly FieldInfo? _controlCenterTrackerField;
        private readonly FieldInfo? _notificationTrackerField;
        private readonly MethodInfo? _closePopupSurfacesExceptMethod;
        private readonly MethodInfo? _updateStatusIconsMethod;
        private readonly MethodInfo? _openControlCenterMethod;
        private readonly MethodInfo? _openMessageCenterMethod;
        private readonly MethodInfo? _trackerStartMethod;
        private readonly StatusNotificationTrayRelay _trayRelay;

        private Visibility _lastHonorYoyoVisibility = Visibility.Visible;
        private bool _isSettingYoyoVisibility;
        private bool _controlCenterClickHooked;
        private bool _notificationClickHooked;
        private long _lastControlCenterFastOpenTick;
        private long _lastNotificationFastOpenTick;
        private bool _disposed;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(uint dwProcessId);

        public TopBarUiEnhancer(Window topBarWindow)
        {
            _window = topBarWindow ?? throw new ArgumentNullException(nameof(topBarWindow));
            _fastControlCenterClickHandler = OnControlCenterButtonClick;
            _fastNotificationClickHandler = OnNotificationButtonClick;

            const BindingFlags flags =
                BindingFlags.Instance |
                BindingFlags.NonPublic |
                BindingFlags.Public;

            Type t = topBarWindow.GetType();
            _yoyoButton = t.GetField("YoyoClawButton", flags)?.GetValue(topBarWindow) as Button;
            _controlCenterButton = t.GetField("ControlCenterButton", flags)?.GetValue(topBarWindow) as Button;
            _notificationButton = t.GetField("NotificationButton", flags)?.GetValue(topBarWindow) as Button;
            _notificationIcon = t.GetField("NotificationIcon", flags)?.GetValue(topBarWindow) as FrameworkElement;
            _statusItemsControl = t.GetField("StatusNotificationItemsControl", flags)?.GetValue(topBarWindow) as ItemsControl;

            _hasNotificationField = t.GetField("_hasNotification", flags);
            _systemServiceField = t.GetField("_systemService", flags);
            _startSharedServicesField = t.GetField("_startSharedServices", flags);
            _controlCenterTrackerField = t.GetField("_controlCenterTracker", flags);
            _notificationTrackerField = t.GetField("_notificationTracker", flags);
            _closePopupSurfacesExceptMethod = t.GetMethod("CloseTopBarPopupSurfacesExcept", flags);
            _updateStatusIconsMethod = t.GetMethod("UpdateStatusIcons", flags);

            object? sysService = _systemServiceField?.GetValue(topBarWindow);
            if (sysService != null)
            {
                Type svcType = sysService.GetType();
                _openControlCenterMethod = svcType.GetMethod("OpenControlCenterTrayOrFallback", flags);
                _openMessageCenterMethod = svcType.GetMethod("OpenHonorMessageCenterOrFallback", flags);
            }

            object? notifTracker = (_notificationTrackerField ?? _controlCenterTrackerField)?.GetValue(topBarWindow);
            if (notifTracker != null)
            {
                _trackerStartMethod = notifTracker.GetType().GetMethod("Start", flags, null, Type.EmptyTypes, null);
            }

            if (_yoyoButton != null)
            {
                _lastHonorYoyoVisibility = _yoyoButton.Visibility;
                _yoyoVisibilityDescriptor = DependencyPropertyDescriptor.FromProperty(
                    UIElement.VisibilityProperty, typeof(UIElement));
                _yoyoVisibilityDescriptor?.AddValueChanged(_yoyoButton, OnYoyoButtonVisibilityChanged);
            }

            if (_controlCenterButton != null)
            {
                _origControlCenterClickMode = _controlCenterButton.ClickMode;
                _origControlCenterClickHandler = CreateClickHandlerDelegate(t, topBarWindow, "ControlCenterButton_Click", flags);
            }

            if (_notificationButton != null)
            {
                _origNotificationClickMode = _notificationButton.ClickMode;
                _origNotificationClickHandler = CreateClickHandlerDelegate(t, topBarWindow, "NotificationButton_Click", flags);
            }

            lock (LifecycleSync)
            {
                ActiveInstances.Add(this);
                _everAttachedToTopBar = true;
            }

            AdaptiveSettingsStore.YoyoClawChanged += OnYoyoClawSettingChanged;
            AdaptiveSettingsStore.NotificationAlertAnimationChanged += OnNotificationAlertSettingChanged;
            AdaptiveSettingsStore.FastSettingsChanged += OnFastSettingsChanged;
            AdaptiveSettingsStore.FastNotificationChanged += OnFastSettingsChanged;

            if (_notificationButton != null)
            {
                _notifBgDescriptor = DependencyPropertyDescriptor.FromProperty(Control.BackgroundProperty, typeof(Button));
                _notifBgDescriptor?.AddValueChanged(_notificationButton, OnNotificationButtonBackgroundChanged);

                if (_notificationIcon != null)
                {
                    Type svgType = _notificationIcon.GetType();
                    var srcField = svgType.GetField("SourceProperty", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    if (srcField?.GetValue(null) is DependencyProperty srcDp)
                    {
                        _notifSourceDescriptor = DependencyPropertyDescriptor.FromProperty(srcDp, svgType);
                        _notifSourceDescriptor?.AddValueChanged(_notificationIcon, OnNotificationIconSourceChanged);
                    }
                }

                _notifAlertTimer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(950)
                };
                _notifAlertTimer.Tick += OnNotificationAlertTimerTick;
                _notifAlertTimer.Start();
            }

            ApplyYoyoVisibility();
            ApplyControlCenterClickMode();
            RefreshNotificationAlertVisual(allowImmediateShake: false);
            _trayRelay = new StatusNotificationTrayRelay(topBarWindow);

            DiagnosticLog.Info(
                $"TopBarUiEnhancer attached (yoyoFound={_yoyoButton != null}, controlCenterFound={_controlCenterButton != null}, " +
                $"notificationFound={_notificationButton != null}, showYoyo={AdaptiveSettingsStore.IsYoyoClawVisible}, " +
                $"fastSettings={AdaptiveSettingsStore.IsFastSettingsResponseEnabled}, " +
                $"fastNotification={AdaptiveSettingsStore.IsFastNotificationResponseEnabled}).");
        }

        internal static bool ShouldAllowSettingsWindowHideOnClose()
        {
            lock (LifecycleSync)
            {
                return !_everAttachedToTopBar || ActiveInstances.Count > 0;
            }
        }

        private void OnYoyoClawSettingChanged(bool _)
        {
            if (_disposed)
            {
                return;
            }

            _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                if (!_disposed)
                {
                    ApplyYoyoVisibility();
                }
            }));
        }

        private void OnFastSettingsChanged(bool _)
        {
            if (_disposed)
            {
                return;
            }

            _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                if (!_disposed)
                {
                    ApplyControlCenterClickMode();
                }
            }));
        }

        private void OnYoyoButtonVisibilityChanged(object? sender, EventArgs e)
        {
            if (_disposed || _isSettingYoyoVisibility || _yoyoButton == null)
            {
                return;
            }

            _lastHonorYoyoVisibility = _yoyoButton.Visibility;
            if (!AdaptiveSettingsStore.IsYoyoClawVisible && _yoyoButton.Visibility != Visibility.Collapsed)
            {
                ApplyYoyoVisibility();
            }
        }

        private void ApplyYoyoVisibility()
        {
            if (_yoyoButton == null)
            {
                return;
            }

            Visibility target = AdaptiveSettingsStore.IsYoyoClawVisible
                ? _lastHonorYoyoVisibility
                : Visibility.Collapsed;

            if (_yoyoButton.Visibility == target)
            {
                return;
            }

            _isSettingYoyoVisibility = true;
            try
            {
                _yoyoButton.Visibility = target;
            }
            finally
            {
                _isSettingYoyoVisibility = false;
            }
        }

        private static RoutedEventHandler? CreateClickHandlerDelegate(
            Type targetType,
            Window targetWindow,
            string methodName,
            BindingFlags flags)
        {
            MethodInfo? m = targetType.GetMethod(methodName, flags);
            if (m == null)
            {
                return null;
            }

            try
            {
                return (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), targetWindow, m);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"TopBarUiEnhancer bind {methodName} failed: {ex.Message}");
                return null;
            }
        }

        private static void SyncFastButtonHook(
            Button? button,
            bool fastEnabled,
            ClickMode origMode,
            RoutedEventHandler? origHandler,
            RoutedEventHandler fastHandler,
            ref bool isHooked)
        {
            if (button == null)
            {
                return;
            }

            ClickMode desired = fastEnabled ? ClickMode.Press : origMode;
            if (button.ClickMode != desired)
            {
                button.ClickMode = desired;
            }

            if (origHandler == null)
            {
                return;
            }

            if (fastEnabled && !isHooked)
            {
                button.Click -= origHandler;
                button.Click += fastHandler;
                isHooked = true;
            }
            else if (!fastEnabled && isHooked)
            {
                button.Click -= fastHandler;
                button.Click += origHandler;
                isHooked = false;
            }
        }

        private void ApplyControlCenterClickMode()
        {
            bool fastSettingsEnabled = AdaptiveSettingsStore.IsFastSettingsResponseEnabled;
            bool fastNotifEnabled = AdaptiveSettingsStore.IsFastNotificationResponseEnabled;

            SyncFastButtonHook(
                _controlCenterButton,
                fastSettingsEnabled,
                _origControlCenterClickMode,
                _origControlCenterClickHandler,
                _fastControlCenterClickHandler,
                ref _controlCenterClickHooked);

            SyncFastButtonHook(
                _notificationButton,
                fastNotifEnabled,
                _origNotificationClickMode,
                _origNotificationClickHandler,
                _fastNotificationClickHandler,
                ref _notificationClickHooked);

            if (fastNotifEnabled)
            {
                ScheduleMessageCenterPrewarmIfNeeded();
            }
        }

        private void CloseOtherPopupSurfaces(int popupSurfaceValue)
        {
            if (_closePopupSurfacesExceptMethod == null)
            {
                return;
            }

            ParameterInfo[] ps = _closePopupSurfacesExceptMethod.GetParameters();
            object?[] args = ps.Length == 1
                ? new object?[]
                {
                    ps[0].ParameterType.IsEnum
                        ? Enum.ToObject(ps[0].ParameterType, popupSurfaceValue)
                        : (ps[0].ParameterType == typeof(int)
                            ? popupSurfaceValue
                            : (ps[0].ParameterType.IsValueType ? Activator.CreateInstance(ps[0].ParameterType) : null))
                }
                : Array.Empty<object?>();
            _closePopupSurfacesExceptMethod.Invoke(_window, args);
        }

        private void PrepareFastPopupOpen(int popupSurfaceValue, FieldInfo? trackerField, bool clearNotificationBadge)
        {
            try
            {
                CloseOtherPopupSurfaces(popupSurfaceValue);

                if (clearNotificationBadge)
                {
                    _hasNotificationField?.SetValue(_window, false);
                }

                if (_startSharedServicesField?.GetValue(_window) is Action startShared)
                {
                    startShared();
                }

                object? tracker = trackerField?.GetValue(_window);
                if (tracker != null && _trackerStartMethod != null)
                {
                    _trackerStartMethod.Invoke(tracker, null);
                }

                if (clearNotificationBadge && _updateStatusIconsMethod != null)
                {
                    ParameterInfo[] ups = _updateStatusIconsMethod.GetParameters();
                    object?[] uargs = ups.Length == 0
                        ? Array.Empty<object?>()
                        : new object?[] { ups[0].IsOptional && ups[0].HasDefaultValue ? ups[0].DefaultValue : null };
                    _updateStatusIconsMethod.Invoke(_window, uargs);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"TopBarUiEnhancer popup UI state sync failed: {ex.Message}");
            }
        }

        private void OnControlCenterButtonClick(object sender, RoutedEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            if (!AdaptiveSettingsStore.IsFastSettingsResponseEnabled)
            {
                _origControlCenterClickHandler?.Invoke(sender, e);
                return;
            }

            long nowTick = Environment.TickCount64;
            if (nowTick - _lastControlCenterFastOpenTick < 140)
            {
                return;
            }
            _lastControlCenterFastOpenTick = nowTick;

            if (HasVisibleControlCenterWindow())
            {
                CloseOtherPopupSurfaces(PopupSurfaceControlCenter);
                if (TryDismissVisibleControlCenterWindow(out string dismissMethod))
                {
                    DiagnosticLog.Info($"ControlCenterButton toggled visible AiDesktopMainWindow closed ({dismissMethod}).");
                    return;
                }
            }

            PrepareFastPopupOpen(PopupSurfaceControlCenter, _controlCenterTrackerField, clearNotificationBadge: false);

            if (TryPostControlCenterTrayOpen(out IntPtr trayHwnd))
            {
                DiagnosticLog.Info($"ControlCenterButton fast-opened ControlCenterTrayUI via direct PostMessage (hwnd=0x{trayHwnd.ToInt64():X}).");
                return;
            }

            object? sysService = _systemServiceField?.GetValue(_window);
            MethodInfo? openMethod = _openControlCenterMethod;
            Task.Run(() => LaunchControlCenterFallback(sysService, openMethod));
        }

        internal static bool TryPostControlCenterTrayOpen(out IntPtr trayHwnd)
        {
            trayHwnd = FindWindowExW(IntPtr.Zero, IntPtr.Zero, ControlCenterTrayClassName, null);
            if (trayHwnd == IntPtr.Zero)
            {
                return false;
            }

            AllowForegroundForWindow(trayHwnd);
            return PostMessageW(
                trayHwnd,
                WmControlCenterTray,
                new IntPtr(ControlCenterTrayIconId),
                new IntPtr(WmLButtonUp));
        }

        internal static bool HasVisibleControlCenterWindow() =>
            FindFirstVisibleWindowByTitle(ControlCenterWindowTitle) != IntPtr.Zero;

        internal static bool TryDismissVisibleControlCenterWindow(out string method)
        {
            IntPtr visibleHwnd = FindFirstVisibleWindowByTitle(ControlCenterWindowTitle);
            if (visibleHwnd == IntPtr.Zero)
            {
                method = "none";
                return false;
            }

            IntPtr trayHwnd = FindWindowExW(IntPtr.Zero, IntPtr.Zero, ControlCenterTrayClassName, null);
            if (trayHwnd != IntPtr.Zero)
            {
                _ = GetWindowThreadProcessId(visibleHwnd, out uint visiblePid);
                _ = GetWindowThreadProcessId(trayHwnd, out uint trayPid);
                if (visiblePid != 0 && visiblePid == trayPid && TryPostControlCenterTrayOpen(out IntPtr postedTrayHwnd))
                {
                    method = $"tray PostMessage hwnd=0x{postedTrayHwnd.ToInt64():X}";
                    return true;
                }
            }

            int dismissed = TryDismissVisibleWindowsByTitle(ControlCenterWindowTitle);
            method = $"WM_ACTIVATE count={dismissed}";
            return dismissed > 0;
        }

        internal static int TryDismissVisibleWindowsByTitle(string title)
        {
            int posted = 0;
            IntPtr hAfter = IntPtr.Zero;
            while (true)
            {
                IntPtr hWnd = FindWindowExW(IntPtr.Zero, hAfter, null, title);
                if (hWnd == IntPtr.Zero)
                {
                    break;
                }

                hAfter = hWnd;
                if (!IsWindowVisible(hWnd))
                {
                    continue;
                }

                if (PostMessageW(hWnd, WmActivate, IntPtr.Zero, IntPtr.Zero))
                {
                    posted++;
                }
            }

            return posted;
        }

        private static void LaunchControlCenterFallback(object? sysService, MethodInfo? openMethod)
        {
            try
            {
                if (sysService != null && openMethod != null)
                {
                    openMethod.Invoke(sysService, null);
                }
                else if (File.Exists(ControlCenterExePath))
                {
                    Process.Start(new ProcessStartInfo(ControlCenterExePath)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"ControlCenterButton async cold-start failed: {ex.Message}");
            }
        }

        private void OnNotificationButtonClick(object sender, RoutedEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            if (!AdaptiveSettingsStore.IsFastNotificationResponseEnabled)
            {
                _origNotificationClickHandler?.Invoke(sender, e);
                return;
            }

            long nowTick = Environment.TickCount64;
            if (nowTick - _lastNotificationFastOpenTick < 140)
            {
                return;
            }
            _lastNotificationFastOpenTick = nowTick;

            bool wasVisibleBefore = HasVisibleMessageCenterWindow();
            if (wasVisibleBefore)
            {
                _hasNotificationField?.SetValue(_window, false);
                RefreshNotificationAlertVisual(allowImmediateShake: false);
                CloseOtherPopupSurfaces(PopupSurfaceNotification);
                long seq = System.Threading.Interlocked.Increment(ref _notifToggleGeneration);
                int dismissed = TryDismissVisibleWindowsByTitle(MessageCenterWindowTitle);
                DiagnosticLog.Info($"NotificationButton toggled visible MessageCenterUI closed via WM_ACTIVATE(WA_INACTIVE) (count={dismissed}).");

                long elapsedSinceOpen = nowTick - _lastNotificationOpenedTick;
                if (elapsedSinceOpen >= 0 && elapsedSinceOpen < 560)
                {
                    int retryDelayMs = (int)Math.Clamp(580 - elapsedSinceOpen, 80, 580);
                    _ = RetryDismissMessageCenterIfStillOpenAsync(seq, retryDelayMs);
                }
                return;
            }

            long openGen = System.Threading.Interlocked.Increment(ref _notifToggleGeneration);
            _lastNotificationOpenedTick = nowTick;
            PrepareFastPopupOpen(PopupSurfaceNotification, _notificationTrackerField, clearNotificationBadge: true);
            RefreshNotificationAlertVisual(allowImmediateShake: false);

            bool posted = TryPostMessageCenterSecondOpen(out int postedCount, out int candidateCount);
            object? sysService = _systemServiceField?.GetValue(_window);
            MethodInfo? openMethod = _openMessageCenterMethod;

            if (posted)
            {
                DiagnosticLog.Info(
                    $"NotificationButton fast-opened MessageCenterUI via single WM_SECONDOPEN " +
                    $"(posted={postedCount}, candidates={candidateCount}).");
                Task.Run(async () =>
                {
                    await Task.Delay(350).ConfigureAwait(false);
                    if (_disposed ||
                        System.Threading.Volatile.Read(ref _notifToggleGeneration) != openGen ||
                        HasVisibleMessageCenterWindow())
                    {
                        return;
                    }

                    DiagnosticLog.Warn(
                        $"MessageCenterUI did not become visible after WM_SECONDOPEN (candidates={candidateCount}); " +
                        "recovering unresponsive instance and launching fresh fallback.");
                    KillUnresponsiveMessageCenterProcesses();
                    LaunchMessageCenterFallback(sysService, openMethod);
                });
                return;
            }

            // Cold-start fallback: run off the UI thread so TopBarWindow never blocks during process creation.
            // Do not kill an existing PCManager-owned MessageCenterUI process when no window was found yet;
            // LaunchMessageCenterFallback lets single-instance /r activation preserve any queued Honor messages.
            Task.Run(() => LaunchMessageCenterFallback(sysService, openMethod));
        }

        private static void LaunchMessageCenterFallback(object? sysService, MethodInfo? openMethod)
        {
            try
            {
                if (sysService != null && openMethod != null)
                {
                    openMethod.Invoke(sysService, null);
                }
                else if (File.Exists(MessageCenterExePath))
                {
                    long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    Process.Start(new ProcessStartInfo(MessageCenterExePath, $"\"magictaskbar-ui.exe\" {unixMs} /r")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"NotificationButton async cold-start failed: {ex.Message}");
            }
        }

        private async Task RetryDismissMessageCenterIfStillOpenAsync(long expectedGeneration, int delayMs)
        {
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                if (_disposed ||
                    System.Threading.Volatile.Read(ref _notifToggleGeneration) != expectedGeneration ||
                    !HasVisibleMessageCenterWindow())
                {
                    return;
                }

                int retried = TryDismissVisibleWindowsByTitle(MessageCenterWindowTitle);
                DiagnosticLog.Info($"NotificationButton retried MessageCenterUI dismiss after open debounce (count={retried}).");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"NotificationButton delayed dismiss retry skipped: {ex.Message}");
            }
        }

        internal static bool HasVisibleMessageCenterWindow() =>
            FindFirstVisibleWindowByTitle(MessageCenterWindowTitle) != IntPtr.Zero;

        private static IntPtr FindFirstVisibleWindowByTitle(string title)
        {
            IntPtr hAfter = IntPtr.Zero;
            while (true)
            {
                IntPtr hWnd = FindWindowExW(IntPtr.Zero, hAfter, null, title);
                if (hWnd == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                hAfter = hWnd;
                if (IsWindowVisible(hWnd))
                {
                    return hWnd;
                }
            }
        }

        private static void AllowForegroundForWindow(IntPtr hWnd)
        {
            _ = GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != 0)
            {
                _ = AllowSetForegroundWindow(pid);
            }
        }

        private static void KillUnresponsiveMessageCenterProcesses()
        {
            if (HasVisibleMessageCenterWindow())
            {
                return;
            }

            try
            {
                foreach (Process proc in Process.GetProcessesByName("MessageCenterUI"))
                {
                    using (proc)
                    {
                        try
                        {
                            if (!proc.HasExited)
                            {
                                DiagnosticLog.Info($"Terminating unresponsive MessageCenterUI PID={proc.Id} before fallback relaunch.");
                                proc.Kill();
                                _ = proc.WaitForExit(800);
                            }
                        }
                        catch (Exception ex)
                        {
                            DiagnosticLog.Warn($"Failed to terminate stuck MessageCenterUI PID={proc.Id}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"KillUnresponsiveMessageCenterProcesses enumeration failed: {ex.Message}");
            }
        }

        internal static bool TryPostMessageCenterSecondOpen(out int postedCount, out int candidateCount)
        {
            postedCount = 0;
            candidateCount = 0;

            IntPtr bestHwnd = IntPtr.Zero;
            int bestHeight = -1;
            IntPtr hAfter = IntPtr.Zero;
            while (true)
            {
                IntPtr hWnd = FindWindowExW(IntPtr.Zero, hAfter, null, MessageCenterWindowTitle);
                if (hWnd == IntPtr.Zero)
                {
                    break;
                }

                hAfter = hWnd;
                candidateCount++;

                int h = 0;
                if (GetWindowRect(hWnd, out RECT rc))
                {
                    h = Math.Max(0, rc.Bottom - rc.Top);
                }

                if (bestHwnd == IntPtr.Zero || h > bestHeight)
                {
                    bestHwnd = hWnd;
                    bestHeight = h;
                }
            }

            if (bestHwnd == IntPtr.Zero)
            {
                return false;
            }

            AllowForegroundForWindow(bestHwnd);

            // Post to only one primary MessageCenterUI HWND: MainWindowAI and MainWindow share the
            // same title, and posting to both causes StartUp() + Acrylic ShowWindow() to run twice (flicker).
            if (PostMessageW(bestHwnd, WmSecondOpen, IntPtr.Zero, IntPtr.Zero))
            {
                postedCount = 1;
            }

            return postedCount > 0;
        }

        private static void ScheduleMessageCenterPrewarmIfNeeded()
        {
            if (System.Threading.Interlocked.Exchange(ref _messageCenterPrewarmScheduled, 1) != 0)
            {
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(150).ConfigureAwait(false);
                    if (!AdaptiveSettingsStore.IsFastNotificationResponseEnabled)
                    {
                        return;
                    }

                    IntPtr existing = FindWindowExW(IntPtr.Zero, IntPtr.Zero, null, MessageCenterWindowTitle);
                    if (existing != IntPtr.Zero || !File.Exists(MessageCenterExePath))
                    {
                        return;
                    }

                    if (Process.GetProcessesByName("MessageCenterUI").Length > 0)
                    {
                        return;
                    }

                    long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    Process.Start(new ProcessStartInfo(MessageCenterExePath, $"\"magictaskbar-ui.exe\" {unixMs} /n")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    DiagnosticLog.Info("Pre-warmed MessageCenterUI.exe in background with /n silent flag.");
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warn($"MessageCenterUI background pre-warm skipped: {ex.Message}");
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _messageCenterPrewarmScheduled, 0);
                }
            });
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _trayRelay.Dispose();
            _notifAlertTimer?.Stop();
            AdaptiveSettingsStore.YoyoClawChanged -= OnYoyoClawSettingChanged;
            AdaptiveSettingsStore.NotificationAlertAnimationChanged -= OnNotificationAlertSettingChanged;
            AdaptiveSettingsStore.FastSettingsChanged -= OnFastSettingsChanged;
            AdaptiveSettingsStore.FastNotificationChanged -= OnFastSettingsChanged;

            if (_notificationButton != null && _notifBgDescriptor != null)
            {
                _notifBgDescriptor.RemoveValueChanged(_notificationButton, OnNotificationButtonBackgroundChanged);
            }

            if (_notificationIcon != null && _notifSourceDescriptor != null)
            {
                _notifSourceDescriptor.RemoveValueChanged(_notificationIcon, OnNotificationIconSourceChanged);
            }

            ClearNotificationAlertVisual();

            if (_yoyoButton != null && _yoyoVisibilityDescriptor != null)
            {
                _yoyoVisibilityDescriptor.RemoveValueChanged(_yoyoButton, OnYoyoButtonVisibilityChanged);
            }

            if (_yoyoButton != null && _yoyoButton.Visibility != _lastHonorYoyoVisibility)
            {
                _isSettingYoyoVisibility = true;
                try
                {
                    _yoyoButton.Visibility = _lastHonorYoyoVisibility;
                }
                finally
                {
                    _isSettingYoyoVisibility = false;
                }
            }

            SyncFastButtonHook(
                _controlCenterButton,
                fastEnabled: false,
                _origControlCenterClickMode,
                _origControlCenterClickHandler,
                _fastControlCenterClickHandler,
                ref _controlCenterClickHooked);

            SyncFastButtonHook(
                _notificationButton,
                fastEnabled: false,
                _origNotificationClickMode,
                _origNotificationClickHandler,
                _fastNotificationClickHandler,
                ref _notificationClickHooked);

            bool noTopBarsLeft;
            lock (LifecycleSync)
            {
                ActiveInstances.Remove(this);
                noTopBarsLeft = _everAttachedToTopBar && ActiveInstances.Count == 0;
            }

            if (noTopBarsLeft)
            {
                // Avoid keeping hidden cached settings windows alive when all TopBar windows have exited.
                _window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    if (!ShouldAllowSettingsWindowHideOnClose() && ActiveInstancesCountZero())
                    {
                        TopBarSettingsFastReuse.CloseHiddenWindows();
                    }
                }));
            }
        }

        private static bool ActiveInstancesCountZero()
        {
            lock (LifecycleSync)
            {
                return ActiveInstances.Count == 0;
            }
        }

        private void OnNotificationAlertSettingChanged(bool _)
        {
            if (_disposed || _window.Dispatcher.HasShutdownStarted)
            {
                return;
            }

            _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                RefreshNotificationAlertVisual(allowImmediateShake: true);
            }));
        }

        private void OnNotificationIconSourceChanged(object? sender, EventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            RefreshNotificationAlertVisual(allowImmediateShake: true);
        }

        private void OnNotificationButtonBackgroundChanged(object? sender, EventArgs e)
        {
            if (_disposed || _isUpdatingNotifVisual || !_notifAlertActive || _notificationButton == null)
            {
                return;
            }

            ApplyNotificationButtonPillTint();
        }

        private void OnNotificationAlertTimerTick(object? sender, EventArgs e)
        {
            if (_disposed || !_window.IsVisible)
            {
                return;
            }

            long now = Environment.TickCount64;
            RefreshNotificationAlertVisual(allowImmediateShake: false);
            if (_notifAlertActive && now - _lastNotifShakeTick >= 1950)
            {
                TriggerNotificationShake(now);
            }

            RefreshUnreadAppShakeVisuals(now);
        }

        internal bool RefreshNotificationAlertVisual(bool allowImmediateShake)
        {
            if (_disposed || _notificationButton == null)
            {
                return false;
            }

            bool sysUnread = _hasNotificationField?.GetValue(_window) is true;
            bool shouldAlert = AdaptiveSettingsStore.IsNotificationAlertAnimationEnabled && sysUnread;

            if (!shouldAlert)
            {
                ClearNotificationAlertVisual();
                return false;
            }

            bool wasActive = _notifAlertActive;
            _notifAlertActive = true;
            SvgIconThemer.SetElementAlertHex(_notificationIcon, NotificationAlertHex);
            ApplyNotificationButtonPillTint();

            if (!wasActive || allowImmediateShake)
            {
                long now = Environment.TickCount64;
                if (!wasActive || now - _lastNotifShakeTick >= 1950)
                {
                    TriggerNotificationShake(now);
                }
            }

            return true;
        }

        private void ApplyNotificationButtonPillTint()
        {
            if (_notificationButton == null)
            {
                return;
            }

            Brush desired = new SolidColorBrush(Color.FromArgb(0x3D, 0xFF, 0x6A, 0x2B));
            desired.Freeze();
            if (_notificationButton.Background is SolidColorBrush cur &&
                cur.Color == Color.FromArgb(0x3D, 0xFF, 0x6A, 0x2B))
            {
                return;
            }

            _isUpdatingNotifVisual = true;
            try
            {
                _notificationButton.Background = desired;
            }
            finally
            {
                _isUpdatingNotifVisual = false;
            }
        }

        private void ClearNotificationAlertVisual()
        {
            bool wasActive = _notifAlertActive;
            _notifAlertActive = false;

            if (_notifShakeRotate != null)
            {
                _notifShakeRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                _notifShakeRotate.Angle = 0.0;
            }

            ClearUnreadAppShakeVisuals();

            if (wasActive && _notificationIcon != null)
            {
                SvgIconThemer.SetElementAlertHex(_notificationIcon, null);
            }

            if (wasActive && _notificationButton != null)
            {
                _isUpdatingNotifVisual = true;
                try
                {
                    _notificationButton.Background = Brushes.Transparent;
                }
                finally
                {
                    _isUpdatingNotifVisual = false;
                }
            }
        }

        private void TriggerNotificationShake(long nowTick)
        {
            _lastNotifShakeTick = nowTick;
            UIElement? target = _notificationIcon ?? (UIElement?)_notificationButton;
            if (target == null)
            {
                return;
            }

            _notifShakeRotate ??= AttachShakeTransform(target);
            StartShakeKeyFrames(_notifShakeRotate);
        }

        private void RefreshUnreadAppShakeVisuals(long nowTick)
        {
            if (!AdaptiveSettingsStore.IsNotificationAlertAnimationEnabled || _statusItemsControl == null)
            {
                ClearUnreadAppShakeVisuals();
                return;
            }

            var activeItems = new HashSet<object>();
            bool dueForShake = nowTick - _lastAppShakeTick >= 1950;
            bool shookAny = false;

            foreach (object? item in _statusItemsControl.Items)
            {
                if (item == null)
                {
                    continue;
                }

                bool hasUnread = item.GetType().GetProperty("HasUnread", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item) is true;
                if (!hasUnread)
                {
                    continue;
                }

                activeItems.Add(item);
                if (_statusItemsControl.ItemContainerGenerator.ContainerFromItem(item) is not UIElement container)
                {
                    continue;
                }

                bool isNew = false;
                if (!_appShakeRotates.TryGetValue(item, out RotateTransform? rt))
                {
                    rt = AttachShakeTransform(container);
                    _appShakeRotates[item] = rt;
                    isNew = true;
                }

                if (isNew || dueForShake)
                {
                    StartShakeKeyFrames(rt);
                    shookAny = true;
                }
            }

            if (shookAny)
            {
                _lastAppShakeTick = nowTick;
            }

            if (_appShakeRotates.Count > 0)
            {
                List<object>? stale = null;
                foreach (KeyValuePair<object, RotateTransform> kv in _appShakeRotates)
                {
                    if (!activeItems.Contains(kv.Key))
                    {
                        kv.Value.BeginAnimation(RotateTransform.AngleProperty, null);
                        kv.Value.Angle = 0.0;
                        stale ??= new List<object>();
                        stale.Add(kv.Key);
                    }
                }

                if (stale != null)
                {
                    foreach (object key in stale)
                    {
                        _appShakeRotates.Remove(key);
                    }
                }
            }
        }

        private void ClearUnreadAppShakeVisuals()
        {
            if (_appShakeRotates.Count == 0)
            {
                return;
            }

            foreach (RotateTransform rt in _appShakeRotates.Values)
            {
                rt.BeginAnimation(RotateTransform.AngleProperty, null);
                rt.Angle = 0.0;
            }
            _appShakeRotates.Clear();
        }

        private static RotateTransform AttachShakeTransform(UIElement target)
        {
            var rotate = new RotateTransform(0.0);
            target.RenderTransformOrigin = new Point(0.5, 0.25);
            if (target.RenderTransform is TransformGroup existingGroup)
            {
                existingGroup.Children.Add(rotate);
            }
            else if (target.RenderTransform != null && !target.RenderTransform.IsFrozen && target.RenderTransform != Transform.Identity)
            {
                var tg = new TransformGroup();
                tg.Children.Add(target.RenderTransform);
                tg.Children.Add(rotate);
                target.RenderTransform = tg;
            }
            else
            {
                target.RenderTransform = rotate;
            }

            return rotate;
        }

        private static void StartShakeKeyFrames(RotateTransform rotate)
        {
            var anim = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(420),
                FillBehavior = FillBehavior.Stop
            };
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(-14.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(14.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(-10.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(8.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(270))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(-4.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(340))));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420))));

            rotate.BeginAnimation(RotateTransform.AngleProperty, anim, HandoffBehavior.SnapshotAndReplace);
        }
    }

    /// <summary>
    /// Converts close on TopBarSettingsWindow into Hide() when FastSettingsResponse is enabled so
    /// Honor's existing WeakReference reuse opens settings almost instantaneously on subsequent clicks.
    /// </summary>
    internal static class TopBarSettingsFastReuse
    {
        private static readonly object Sync = new();
        private static readonly HashSet<Window> Attached = new();
        private static readonly HashSet<Window> ForceCloseSet = new();
        private static bool _subscribed;

        public static void Attach(Window settingsWindow)
        {
            if (settingsWindow == null)
            {
                return;
            }

            lock (Sync)
            {
                if (!Attached.Add(settingsWindow))
                {
                    return;
                }

                if (!_subscribed)
                {
                    _subscribed = true;
                    AdaptiveSettingsStore.FastSettingsChanged += OnFastSettingsChanged;
                }
            }

            settingsWindow.Closing += OnSettingsWindowClosing;
            settingsWindow.Closed += OnSettingsWindowClosed;
            settingsWindow.Dispatcher.ShutdownStarted += OnDispatcherShutdownStarted;
        }

        public static void CloseHiddenWindows()
        {
            List<Window> toClose = new();
            lock (Sync)
            {
                foreach (Window w in Attached)
                {
                    if (!w.IsVisible)
                    {
                        ForceCloseSet.Add(w);
                        toClose.Add(w);
                    }
                }
            }

            foreach (Window w in toClose)
            {
                if (w.Dispatcher.HasShutdownStarted)
                {
                    continue;
                }

                w.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    try
                    {
                        w.Close();
                    }
                    catch
                    {
                        // Window already closed.
                    }
                }));
            }
        }

        private static void OnFastSettingsChanged(bool enabled)
        {
            if (!enabled)
            {
                CloseHiddenWindows();
            }
        }

        private static void OnDispatcherShutdownStarted(object? sender, EventArgs e)
        {
            CloseHiddenWindows();
        }

        private static void OnSettingsWindowClosing(object? sender, CancelEventArgs e)
        {
            if (sender is not Window w || e.Cancel)
            {
                return;
            }

            lock (Sync)
            {
                if (ForceCloseSet.Remove(w))
                {
                    return;
                }
            }

            if (!AdaptiveSettingsStore.IsFastSettingsResponseEnabled ||
                w.Dispatcher.HasShutdownStarted ||
                Environment.HasShutdownStarted ||
                !TopBarUiEnhancer.ShouldAllowSettingsWindowHideOnClose())
            {
                return;
            }

            e.Cancel = true;
            w.Hide();
            DiagnosticLog.Info("TopBarSettingsFastReuse hid settings window instead of closing for instant reopen.");
        }

        private static void OnSettingsWindowClosed(object? sender, EventArgs e)
        {
            if (sender is not Window w)
            {
                return;
            }

            w.Closing -= OnSettingsWindowClosing;
            w.Closed -= OnSettingsWindowClosed;
            w.Dispatcher.ShutdownStarted -= OnDispatcherShutdownStarted;

            lock (Sync)
            {
                Attached.Remove(w);
                ForceCloseSet.Remove(w);
            }
        }
    }
}

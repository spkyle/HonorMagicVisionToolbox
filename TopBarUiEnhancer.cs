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
        private readonly DependencyPropertyDescriptor? _yoyoVisibilityDescriptor;
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
            AdaptiveSettingsStore.FastSettingsChanged += OnFastSettingsChanged;
            AdaptiveSettingsStore.FastNotificationChanged += OnFastSettingsChanged;

            ApplyYoyoVisibility();
            ApplyControlCenterClickMode();
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

        private void PrepareFastPopupOpen(int popupSurfaceValue, FieldInfo? trackerField, bool clearNotificationBadge)
        {
            try
            {
                if (_closePopupSurfacesExceptMethod != null)
                {
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

            PrepareFastPopupOpen(PopupSurfaceControlCenter, _controlCenterTrackerField, clearNotificationBadge: false);

            if (HasVisibleControlCenterWindow())
            {
                DiagnosticLog.Info("ControlCenterButton clicked while AiDesktopMainWindow visible; skipping duplicate tray toggle.");
                return;
            }

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
            HasVisibleWindowByTitle(ControlCenterWindowTitle);

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

            PrepareFastPopupOpen(PopupSurfaceNotification, _notificationTrackerField, clearNotificationBadge: true);

            bool wasVisibleBefore = HasVisibleMessageCenterWindow();
            if (wasVisibleBefore)
            {
                // Activating TopBar on click already deactivates and hides the open MessageCenterUI window;
                // do not re-post WM_SECONDOPEN or it fights Deactivated and causes a visible flicker.
                DiagnosticLog.Info("NotificationButton clicked while MessageCenterUI visible; skipping duplicate WM_SECONDOPEN.");
                return;
            }

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
                    if (_disposed || HasVisibleMessageCenterWindow())
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
            Task.Run(() =>
            {
                KillUnresponsiveMessageCenterProcesses();
                LaunchMessageCenterFallback(sysService, openMethod);
            });
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

        internal static bool HasVisibleMessageCenterWindow() =>
            HasVisibleWindowByTitle(MessageCenterWindowTitle);

        private static bool HasVisibleWindowByTitle(string title)
        {
            IntPtr hAfter = IntPtr.Zero;
            while (true)
            {
                IntPtr hWnd = FindWindowExW(IntPtr.Zero, hAfter, null, title);
                if (hWnd == IntPtr.Zero)
                {
                    return false;
                }

                hAfter = hWnd;
                if (IsWindowVisible(hWnd))
                {
                    return true;
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
            AdaptiveSettingsStore.YoyoClawChanged -= OnYoyoClawSettingChanged;
            AdaptiveSettingsStore.FastSettingsChanged -= OnFastSettingsChanged;
            AdaptiveSettingsStore.FastNotificationChanged -= OnFastSettingsChanged;

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

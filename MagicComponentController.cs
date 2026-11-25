using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TopBarAdaptiveColor
{
    /// <summary>
    /// Independently controls Honor Magic Vision's TopBar (MagicTaskbarWpf.TopBarWindow) and Bottom Dock
    /// (MagicTaskbarWpf.MainWindow) hosted inside magictaskbar-ui.exe (MagicTaskbarWpf.HostApp), allowing
    /// users to freely combine Desktop / TopBar / Dock without modifying any official binaries:
    /// - Disabling TopBar: sets HostApp._launchTopBar = false, cleanly closes all TopBarWindow instances
    ///   (releasing top AppBar workarea via TopBarWindow_Closed) without exiting HostApp, and displays a
    ///   lightweight native system tray icon ("Magic 视界工具箱") so the user can always reopen Settings
    ///   or re-enable TopBar with one click.
    /// - Disabling Dock: sets HostApp._launchDock = false, cleanly closes all Dock MainWindow instances
    ///   (releasing bottom AppBar & restoring native Windows Shell_TrayWnd via ShellTaskbarController),
    ///   and guards taskbar visibility across startup races and display topology changes.
    /// - Re-enabling either component restores _launchTopBar / _launchDock = true and invokes HostApp's
    ///   official ReconcileTopBarWindows / ReconcileDockWindows across all monitors.
    /// </summary>
    internal static class MagicComponentController
    {
        private const string TopBarWindowTypeName = "MagicTaskbarWpf.TopBarWindow";
        private const string DockMainWindowTypeName = "MagicTaskbarWpf.MainWindow";
        private const string TopBarSettingsWindowTypeName = "MagicTaskbarWpf.TopBarSettingsWindow";

        private static int _initialized;
        private static int _processExiting;

        private static Application? _hostApp;
        private static Dispatcher? _uiDispatcher;

        // Cached HostApp reflection handles
        private static FieldInfo? _launchTopBarField;
        private static FieldInfo? _launchDockField;
        private static FieldInfo? _topBarWindowsField;
        private static FieldInfo? _dockWindowsField;
        private static MethodInfo? _hostWindowClosedMethod;
        private static MethodInfo? _getOrderedMonitorsMethod;
        private static MethodInfo? _reconcileTopBarMethod;
        private static MethodInfo? _reconcileDockMethod;

        // Cached SettingsStateService so Settings can always reopen even when all TopBarWindows are closed
        private static object? _cachedSettingsStateService;

        // Lightweight native tray icon shown ONLY while TopBar is disabled
        private static ToolboxHelperTrayIcon? _helperTrayIcon;

        public static void Initialize(Application? hostApp)
        {
            if (hostApp == null || Interlocked.Exchange(ref _initialized, 1) == 1)
            {
                return;
            }

            _hostApp = hostApp;
            _uiDispatcher = hostApp.Dispatcher;

            // Keep magictaskbar-ui alive even if both TopBar and Dock windows are disabled by user choice.
            try
            {
                hostApp.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }
            catch
            {
            }

            BindHostAppReflection(hostApp);

            AdaptiveSettingsStore.DisableTopBarChanged += OnDisableTopBarChanged;
            AdaptiveSettingsStore.DisableDockChanged += OnDisableDockChanged;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            try
            {
                SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"MagicComponentController DisplaySettingsChanged hook skipped: {ex.Message}");
            }

            bool disableTopBar = AdaptiveSettingsStore.IsTopBarDisabled;
            bool disableDock = AdaptiveSettingsStore.IsDockDisabled;

            if (disableTopBar)
            {
                ApplyTopBarStateOnUi(disableTopBar: true, isStartup: true);
            }

            if (disableDock)
            {
                ApplyDockStateOnUi(disableDock: true, isStartup: true);
            }

            DiagnosticLog.Info(
                $"MagicComponentController initialized (disableTopBar={disableTopBar}, disableDock={disableDock}, " +
                $"reconcileTopBar={_reconcileTopBarMethod != null}, reconcileDock={_reconcileDockMethod != null}).");
        }

        public static void CaptureSettingsServiceFromWindow(Window window)
        {
            if (window == null || _cachedSettingsStateService != null)
            {
                return;
            }

            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                Type t = window.GetType();
                string? fullName = t.FullName;

                if (string.Equals(fullName, TopBarWindowTypeName, StringComparison.Ordinal))
                {
                    object? menuSvc = t.GetField("_honorMenuService", flags)?.GetValue(window);
                    if (menuSvc != null)
                    {
                        object? stateSvc = menuSvc.GetType().GetProperty("SettingsStateService", flags)?.GetValue(menuSvc)
                            ?? menuSvc.GetType().GetField("_settingsStateService", flags)?.GetValue(menuSvc);
                        if (stateSvc != null)
                        {
                            _cachedSettingsStateService = stateSvc;
                        }
                    }
                }
                else if (string.Equals(fullName, TopBarSettingsWindowTypeName, StringComparison.Ordinal))
                {
                    object? stateSvc = t.GetField("_settingsService", flags)?.GetValue(window);
                    if (stateSvc != null)
                    {
                        _cachedSettingsStateService = stateSvc;
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Called from TopBarHookCoordinator when any window loads, catching in-flight startup windows
        /// created just before _launchTopBar / _launchDock was set to false.
        /// </summary>
        public static bool ShouldSuppressWindow(Window window)
        {
            if (window == null || Volatile.Read(ref _processExiting) == 1)
            {
                return false;
            }

            CaptureSettingsServiceFromWindow(window);

            string? fullName = window.GetType().FullName;
            if (string.Equals(fullName, TopBarWindowTypeName, StringComparison.Ordinal) &&
                AdaptiveSettingsStore.IsTopBarDisabled)
            {
                DiagnosticLog.Info("MagicComponentController: suppressing TopBarWindow created while DisableTopBar=true.");
                CloseManagedHostWindow(window, isTopBar: true);
                EnsureHelperTrayIconState();
                return true;
            }

            if (string.Equals(fullName, DockMainWindowTypeName, StringComparison.Ordinal) &&
                AdaptiveSettingsStore.IsDockDisabled)
            {
                DiagnosticLog.Info("MagicComponentController: suppressing Dock MainWindow created while DisableDock=true.");
                CloseManagedHostWindow(window, isTopBar: false);
                ScheduleNativeShellTaskbarVerify(delayMs: 250);
                return true;
            }

            return false;
        }

        private static void BindHostAppReflection(Application hostApp)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type t = hostApp.GetType();
            _launchTopBarField = t.GetField("_launchTopBar", flags);
            _launchDockField = t.GetField("_launchDock", flags);
            _topBarWindowsField = t.GetField("_topBarWindows", flags);
            _dockWindowsField = t.GetField("_dockWindows", flags);
            _hostWindowClosedMethod = t.GetMethod("Window_Closed", flags);
            _getOrderedMonitorsMethod = t.GetMethod("GetOrderedDisplayMonitors", flags);
            _reconcileTopBarMethod = t.GetMethod("ReconcileTopBarWindows", flags);
            _reconcileDockMethod = t.GetMethod("ReconcileDockWindows", flags);
        }

        private static void OnDisableTopBarChanged(bool disabled)
        {
            if (Volatile.Read(ref _processExiting) == 1)
            {
                return;
            }

            DispatchToUi(() => ApplyTopBarStateOnUi(disabled, isStartup: false));
        }

        private static void OnDisableDockChanged(bool disabled)
        {
            if (Volatile.Read(ref _processExiting) == 1)
            {
                return;
            }

            DispatchToUi(() => ApplyDockStateOnUi(disabled, isStartup: false));
        }

        private static void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            if (Volatile.Read(ref _processExiting) == 1)
            {
                return;
            }

            if (AdaptiveSettingsStore.IsDockDisabled)
            {
                // HostApp.SyncDisplayWindows can momentarily hide Shell_TrayWnd during display topology transitions.
                ScheduleNativeShellTaskbarVerify(delayMs: 400);
                ScheduleNativeShellTaskbarVerify(delayMs: 1200);
            }
        }

        private static void OnProcessExit(object? sender, EventArgs e)
        {
            Volatile.Write(ref _processExiting, 1);
            try
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            }
            catch
            {
            }

            try
            {
                _helperTrayIcon?.Dispose();
                _helperTrayIcon = null;
            }
            catch
            {
            }
        }

        private static void ApplyTopBarStateOnUi(bool disableTopBar, bool isStartup)
        {
            Application? app = _hostApp;
            if (app == null || Volatile.Read(ref _processExiting) == 1)
            {
                return;
            }

            try
            {
                SetHostBoolField(_launchTopBarField, !disableTopBar);

                if (disableTopBar)
                {
                    List<Window> topBars = CollectHostWindows(_topBarWindowsField, TopBarWindowTypeName);
                    foreach (Window w in topBars)
                    {
                        CaptureSettingsServiceFromWindow(w);
                        CloseManagedHostWindow(w, isTopBar: true);
                    }

                    EnsureHelperTrayIconState();
                    DiagnosticLog.Info($"MagicComponentController: TopBar disabled (closed={topBars.Count}, startup={isStartup}).");
                }
                else
                {
                    EnsureHelperTrayIconState();

                    List<Window> existing = CollectHostWindows(_topBarWindowsField, TopBarWindowTypeName);
                    if (existing.Count == 0)
                    {
                        InvokeReconcileMethod(_reconcileTopBarMethod, "toolbox-topbar-enable");
                    }

                    DiagnosticLog.Info($"MagicComponentController: TopBar enabled (existingBefore={existing.Count}).");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"MagicComponentController ApplyTopBarStateOnUi(disable={disableTopBar}) failed: {ex}");
            }
        }

        private static void ApplyDockStateOnUi(bool disableDock, bool isStartup)
        {
            Application? app = _hostApp;
            if (app == null || Volatile.Read(ref _processExiting) == 1)
            {
                return;
            }

            try
            {
                SetHostBoolField(_launchDockField, !disableDock);

                if (disableDock)
                {
                    List<Window> docks = CollectHostWindows(_dockWindowsField, DockMainWindowTypeName);
                    foreach (Window d in docks)
                    {
                        CloseManagedHostWindow(d, isTopBar: false);
                    }

                    RestoreNativeShellTaskbarsBestEffort();
                    ScheduleNativeShellTaskbarVerify(delayMs: isStartup ? 800 : 300);
                    if (isStartup)
                    {
                        ScheduleNativeShellTaskbarVerify(delayMs: 2000);
                    }

                    DiagnosticLog.Info($"MagicComponentController: Dock disabled & native taskbar restored (closed={docks.Count}, startup={isStartup}).");
                }
                else
                {
                    List<Window> existing = CollectHostWindows(_dockWindowsField, DockMainWindowTypeName);
                    if (existing.Count == 0)
                    {
                        InvokeReconcileMethod(_reconcileDockMethod, "toolbox-dock-enable");
                    }

                    DiagnosticLog.Info($"MagicComponentController: Dock enabled (existingBefore={existing.Count}).");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"MagicComponentController ApplyDockStateOnUi(disable={disableDock}) failed: {ex}");
            }
        }

        private static void SetHostBoolField(FieldInfo? field, bool value)
        {
            if (field == null || _hostApp == null)
            {
                return;
            }

            try
            {
                field.SetValue(_hostApp, value);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"MagicComponentController SetHostBoolField({field.Name}={value}) failed: {ex.Message}");
            }
        }

        private static List<Window> CollectHostWindows(FieldInfo? listField, string fullTypeName)
        {
            var result = new List<Window>();
            if (_hostApp != null && listField?.GetValue(_hostApp) is IEnumerable items)
            {
                foreach (object? obj in items)
                {
                    if (obj is Window w && !result.Contains(w))
                    {
                        result.Add(w);
                    }
                }
            }

            if (_hostApp != null)
            {
                foreach (Window w in _hostApp.Windows)
                {
                    if (string.Equals(w.GetType().FullName, fullTypeName, StringComparison.Ordinal) &&
                        !result.Contains(w))
                    {
                        result.Add(w);
                    }
                }
            }

            return result;
        }

        private static void CloseManagedHostWindow(Window window, bool isTopBar)
        {
            Application? app = _hostApp;
            if (app == null || window == null)
            {
                return;
            }

            try
            {
                // Keep app alive when closing the last TopBar/Dock window by removing HostApp.Window_Closed first.
                if (_hostWindowClosedMethod != null)
                {
                    try
                    {
                        var del = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), app, _hostWindowClosedMethod);
                        window.Closed -= del;
                    }
                    catch
                    {
                    }
                }

                // Remove from HostApp tracking list before Close() so HostApp never triggers RequestGracefulExit.
                FieldInfo? listField = isTopBar ? _topBarWindowsField : _dockWindowsField;
                if (listField?.GetValue(app) is IList list)
                {
                    try
                    {
                        list.Remove(window);
                    }
                    catch
                    {
                    }
                }

                if (!isTopBar)
                {
                    // Ensure Dock MainWindow.OnClosed restores Shell_TrayWnd via _shellTaskbar.Dispose().
                    try
                    {
                        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                        window.GetType().GetField("_skipShellTaskbarRestoreOnClose", flags)?.SetValue(window, false);
                    }
                    catch
                    {
                    }
                }

                if (ReferenceEquals(app.MainWindow, window))
                {
                    try
                    {
                        app.MainWindow = null;
                    }
                    catch
                    {
                    }
                }

                window.Close();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"MagicComponentController CloseManagedHostWindow({window.GetType().Name}) error: {ex.Message}");
            }
        }

        private static void InvokeReconcileMethod(MethodInfo? reconcileMethod, string reason)
        {
            if (_hostApp == null || reconcileMethod == null || _getOrderedMonitorsMethod == null)
            {
                DiagnosticLog.Warn($"MagicComponentController: reconcile reflection unavailable for {reason}.");
                return;
            }

            try
            {
                object? monitors = _getOrderedMonitorsMethod.Invoke(
                    _getOrderedMonitorsMethod.IsStatic ? null : _hostApp,
                    null);
                if (monitors == null)
                {
                    DiagnosticLog.Warn($"MagicComponentController: GetOrderedDisplayMonitors returned null for {reason}.");
                    return;
                }

                ParameterInfo[] ps = reconcileMethod.GetParameters();
                object?[] args = new object?[ps.Length];
                if (ps.Length >= 1) args[0] = monitors;
                if (ps.Length >= 2) args[1] = reason;
                if (ps.Length >= 3) args[2] = Convert.ChangeType(0, ps[2].ParameterType);

                reconcileMethod.Invoke(_hostApp, args);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"MagicComponentController InvokeReconcileMethod({reason}) failed: {ex}");
            }
        }

        private static void ScheduleNativeShellTaskbarVerify(int delayMs)
        {
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try
                {
                    Thread.Sleep(delayMs);
                    if (Volatile.Read(ref _processExiting) == 0 && AdaptiveSettingsStore.IsDockDisabled)
                    {
                        RestoreNativeShellTaskbarsBestEffort();
                    }
                }
                catch
                {
                }
            }, null);
        }

        private static void RestoreNativeShellTaskbarsBestEffort()
        {
            try
            {
                // First invoke Honor's own ShellTaskbarController.RestoreShellTaskbarsBestEffort() if loaded
                // so any PrivilegedHelper taskbar visual lease is cleanly released as well.
                Assembly? dockAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "MagicTaskbar.Dock", StringComparison.OrdinalIgnoreCase));
                Type? shellCtrlType = dockAsm?.GetType("MagicTaskbarWpf.ShellTaskbarController");
                MethodInfo? restoreMethod = shellCtrlType?.GetMethod(
                    "RestoreShellTaskbarsBestEffort",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                restoreMethod?.Invoke(null, null);
            }
            catch
            {
            }

            try
            {
                EnumWindows((hwnd, lParam) =>
                {
                    string cls = GetWindowClassName(hwnd);
                    if (string.Equals(cls, "Shell_TrayWnd", StringComparison.Ordinal) ||
                        string.Equals(cls, "Shell_SecondaryTrayWnd", StringComparison.Ordinal))
                    {
                        if (!IsWindowVisible(hwnd))
                        {
                            _ = ShowWindowAsync(hwnd, SwShow);
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
            }
        }

        private static void EnsureHelperTrayIconState()
        {
            bool needTray = AdaptiveSettingsStore.IsTopBarDisabled && Volatile.Read(ref _processExiting) == 0;
            if (needTray)
            {
                if (_helperTrayIcon == null)
                {
                    _helperTrayIcon = new ToolboxHelperTrayIcon();
                }
            }
            else if (_helperTrayIcon != null)
            {
                try
                {
                    _helperTrayIcon.Dispose();
                }
                catch
                {
                }
                _helperTrayIcon = null;
            }
        }

        internal static void OpenToolboxSettingsWindowOnUi()
        {
            DispatchToUi(() =>
            {
                try
                {
                    Application? app = _hostApp ?? Application.Current;
                    if (app == null)
                    {
                        return;
                    }

                    // 1. Reuse existing TopBarSettingsWindow if already instantiated (visible or hidden).
                    Window? existing = null;
                    foreach (Window w in app.Windows)
                    {
                        if (string.Equals(w.GetType().FullName, TopBarSettingsWindowTypeName, StringComparison.Ordinal))
                        {
                            existing = w;
                            break;
                        }
                    }

                    if (existing != null)
                    {
                        CaptureSettingsServiceFromWindow(existing);
                        TopBarSettingsInjector.Attach(existing);
                        if (!existing.IsVisible)
                        {
                            existing.Show();
                        }
                        if (existing.WindowState == WindowState.Minimized)
                        {
                            existing.WindowState = WindowState.Normal;
                        }
                        existing.Activate();
                        TopBarSettingsInjector.TrySelectToolboxTab(existing);
                        return;
                    }

                    // 2. Otherwise instantiate TopBarSettingsWindow directly using cached or fresh SettingsStateService.
                    Assembly? topBarAsm = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => string.Equals(a.GetName().Name, "MagicTaskbar.TopBar", StringComparison.OrdinalIgnoreCase));
                    Type? settingsWinType = topBarAsm?.GetType(TopBarSettingsWindowTypeName);
                    if (settingsWinType == null)
                    {
                        DiagnosticLog.Warn("MagicComponentController: TopBarSettingsWindow type not loaded.");
                        return;
                    }

                    ConstructorInfo? ctor = settingsWinType
                        .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .OrderBy(c => c.GetParameters().Length)
                        .FirstOrDefault();
                    if (ctor == null)
                    {
                        return;
                    }

                    ParameterInfo[] ctorParams = ctor.GetParameters();
                    object?[] args = new object?[ctorParams.Length];
                    if (ctorParams.Length == 1)
                    {
                        object? svc = _cachedSettingsStateService;
                        if (svc == null || !ctorParams[0].ParameterType.IsInstanceOfType(svc))
                        {
                            svc = Activator.CreateInstance(ctorParams[0].ParameterType, nonPublic: true);
                            _cachedSettingsStateService = svc;
                        }
                        args[0] = svc;
                    }

                    if (ctor.Invoke(args) is Window newSettingsWin)
                    {
                        newSettingsWin.Loaded += (_, _) =>
                        {
                            TopBarSettingsInjector.Attach(newSettingsWin);
                            TopBarSettingsInjector.TrySelectToolboxTab(newSettingsWin);
                        };
                        newSettingsWin.Show();
                        newSettingsWin.Activate();
                        DiagnosticLog.Info("MagicComponentController: opened TopBarSettingsWindow from helper tray icon.");
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warn($"MagicComponentController OpenToolboxSettingsWindowOnUi failed: {ex}");
                }
            });
        }

        private static void DispatchToUi(Action action)
        {
            try
            {
                Dispatcher? disp = _uiDispatcher ?? Application.Current?.Dispatcher;
                if (disp == null || disp.HasShutdownStarted || disp.HasShutdownFinished)
                {
                    return;
                }

                if (disp.CheckAccess())
                {
                    action();
                }
                else
                {
                    disp.BeginInvoke(DispatcherPriority.Normal, action);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"MagicComponentController DispatchToUi skipped: {ex.Message}");
            }
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(128);
            int len = GetClassNameW(hwnd, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : string.Empty;
        }

        /// <summary>
        /// Zero-dependency native Win32 Shell_NotifyIconW helper shown ONLY when TopBar is disabled so the
        /// user always retains a 1-click entry to open Magic Vision Toolbox Settings or restore components.
        /// </summary>
        private sealed class ToolboxHelperTrayIcon : IDisposable
        {
            private const uint TrayIconId = 0x5442; // 'TB'
            private const uint WmTrayCallback = 0x8000 + 0x42; // WM_APP + 0x42
            private const uint CmdOpenSettings = 1001;
            private const uint CmdRestoreTopBar = 1002;
            private const uint CmdRestoreDock = 1003;
            private const uint CmdRestoreDesktop = 1004;

            private readonly HwndSource _hwndSource;
            private readonly uint _taskbarCreatedMsg;
            private IntPtr _iconHandle;
            private bool _ownsIconHandle;
            private bool _added;
            private bool _disposed;

            public ToolboxHelperTrayIcon()
            {
                var p = new HwndSourceParameters("MagicToolboxHelperTraySink")
                {
                    Width = 0,
                    Height = 0,
                    PositionX = 0,
                    PositionY = 0,
                    WindowStyle = 0,
                    ExtendedWindowStyle = 0
                };
                _hwndSource = new HwndSource(p);
                _hwndSource.AddHook(WndProc);
                _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

                LoadTrayIconHandle();
                RegisterIcon();
            }

            private void LoadTrayIconHandle()
            {
                try
                {
                    string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        ushort iconIndex = 0;
                        IntPtr hIcon = ExtractAssociatedIconW(IntPtr.Zero, new StringBuilder(exePath, 260), ref iconIndex);
                        if (hIcon != IntPtr.Zero)
                        {
                            _iconHandle = hIcon;
                            _ownsIconHandle = true;
                            return;
                        }
                    }
                }
                catch
                {
                }

                _iconHandle = LoadIconW(IntPtr.Zero, new IntPtr(IdiApplication));
                _ownsIconHandle = false;
            }

            private void RegisterIcon()
            {
                if (_disposed || _hwndSource.Handle == IntPtr.Zero)
                {
                    return;
                }

                bool isZh = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
                var nid = new NOTIFYICONDATAW
                {
                    cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                    hWnd = _hwndSource.Handle,
                    uID = TrayIconId,
                    uFlags = NifMessage | NifIcon | NifTip,
                    uCallbackMessage = WmTrayCallback,
                    hIcon = _iconHandle,
                    szTip = isZh
                        ? "Magic 视界工具箱（点击打开设置）"
                        : "Magic Vision Toolbox (Click for Settings)"
                };

                _added = Shell_NotifyIconW(NimAdd, ref nid);
                DiagnosticLog.Info($"MagicComponentController: helper tray icon registered (ok={_added}).");
            }

            private void UnregisterIcon()
            {
                if (!_added || _hwndSource.Handle == IntPtr.Zero)
                {
                    return;
                }

                _added = false;
                var nid = new NOTIFYICONDATAW
                {
                    cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                    hWnd = _hwndSource.Handle,
                    uID = TrayIconId
                };
                _ = Shell_NotifyIconW(NimDelete, ref nid);
            }

            private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                if (_disposed)
                {
                    return IntPtr.Zero;
                }

                if (_taskbarCreatedMsg != 0 && (uint)msg == _taskbarCreatedMsg)
                {
                    RegisterIcon();
                    return IntPtr.Zero;
                }

                if ((uint)msg == WmTrayCallback)
                {
                    int mouseMsg = unchecked((int)(lParam.ToInt64() & 0xFFFF));
                    if (mouseMsg == WmLButtonUp || mouseMsg == WmLButtonDblClk)
                    {
                        handled = true;
                        OpenToolboxSettingsWindowOnUi();
                    }
                    else if (mouseMsg == WmRButtonUp || mouseMsg == WmContextMenu)
                    {
                        handled = true;
                        ShowContextMenu();
                    }
                }

                return IntPtr.Zero;
            }

            private void ShowContextMenu()
            {
                if (_disposed || _hwndSource.Handle == IntPtr.Zero)
                {
                    return;
                }

                IntPtr hMenu = CreatePopupMenu();
                if (hMenu == IntPtr.Zero)
                {
                    return;
                }

                try
                {
                    bool isZh = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
                    _ = AppendMenuW(hMenu, MfString, new UIntPtr(CmdOpenSettings), isZh ? "打开 Magic 视界工具箱设置" : "Open Magic Toolbox Settings");
                    _ = SetMenuDefaultItem(hMenu, CmdOpenSettings, fByPos: 0);
                    _ = AppendMenuW(hMenu, MfSeparator, UIntPtr.Zero, null);
                    _ = AppendMenuW(hMenu, MfString, new UIntPtr(CmdRestoreTopBar), isZh ? "恢复顶部状态栏" : "Restore Top Status Bar");

                    if (AdaptiveSettingsStore.IsDockDisabled)
                    {
                        _ = AppendMenuW(hMenu, MfString, new UIntPtr(CmdRestoreDock), isZh ? "恢复底部 Dock 栏" : "Restore Bottom Dock");
                    }

                    if (AdaptiveSettingsStore.IsNativeWindowsDesktopEnabled)
                    {
                        _ = AppendMenuW(hMenu, MfString, new UIntPtr(CmdRestoreDesktop), isZh ? "恢复荣耀桌面接管" : "Restore HONOR Desktop");
                    }

                    _ = GetCursorPos(out POINT pt);
                    _ = SetForegroundWindow(_hwndSource.Handle);
                    uint cmd = TrackPopupMenuEx(
                        hMenu,
                        TpmReturnCmd | TpmRightButton | TpmNonotify,
                        pt.X,
                        pt.Y,
                        _hwndSource.Handle,
                        IntPtr.Zero);
                    _ = PostMessageW(_hwndSource.Handle, 0, IntPtr.Zero, IntPtr.Zero);

                    switch (cmd)
                    {
                        case CmdOpenSettings:
                            OpenToolboxSettingsWindowOnUi();
                            break;
                        case CmdRestoreTopBar:
                            AdaptiveSettingsStore.SetModuleEnabled(ToolboxModuleIds.DisableTopBar, false);
                            break;
                        case CmdRestoreDock:
                            AdaptiveSettingsStore.SetModuleEnabled(ToolboxModuleIds.DisableDock, false);
                            break;
                        case CmdRestoreDesktop:
                            AdaptiveSettingsStore.SetModuleEnabled(ToolboxModuleIds.NativeWindowsDesktop, false);
                            break;
                    }
                }
                finally
                {
                    _ = DestroyMenu(hMenu);
                }
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                UnregisterIcon();
                try
                {
                    _hwndSource.RemoveHook(WndProc);
                    _hwndSource.Dispose();
                }
                catch
                {
                }

                if (_ownsIconHandle && _iconHandle != IntPtr.Zero)
                {
                    _ = DestroyIcon(_iconHandle);
                    _iconHandle = IntPtr.Zero;
                    _ownsIconHandle = false;
                }
            }
        }

        #region Win32 Interop

        private const int SwShow = 5;
        private const int IdiApplication = 32512;

        private const uint NimAdd = 0x00000000;
        private const uint NimDelete = 0x00000002;
        private const uint NifMessage = 0x00000001;
        private const uint NifIcon = 0x00000002;
        private const uint NifTip = 0x00000004;

        private const int WmLButtonUp = 0x0202;
        private const int WmLButtonDblClk = 0x0203;
        private const int WmRButtonUp = 0x0205;
        private const int WmContextMenu = 0x007B;

        private const uint MfString = 0x00000000;
        private const uint MfSeparator = 0x00000800;
        private const uint TpmRightButton = 0x0002;
        private const uint TpmNonotify = 0x0080;
        private const uint TpmReturnCmd = 0x0100;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ExtractAssociatedIconW(IntPtr hInst, StringBuilder lpIconPath, ref ushort lpiIcon);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessageW(string lpString);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        #endregion
    }
}

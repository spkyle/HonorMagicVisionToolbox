using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TopBarAdaptiveColor
{
    /// <summary>
    /// Accelerates Honor Magic Vision desktop right-click responsiveness without modifying any official Honor binaries:
    /// 1. Disables Windows 11 EcoQoS / Efficiency Mode throttling and low memory priority on DesktopHost.exe,
    ///    MenuHostNative.exe, and CardMenuHostNative.exe, and elevates their scheduling priority so hidden WinUI 3
    ///    menu hosts stay warm on P-cores with resident XAML pages.
    /// 2. Watches MenuHostNative.exe exit events (e.g., after self-recycle when lazy shell preload was active) and
    ///    immediately pre-launches a fresh Medium-IL MenuHostNative.exe using DesktopHost.exe's own user token,
    ///    eliminating 600-2000ms cold-start stalls and 1500ms ready-watchdog suppression on the next right-click.
    /// 3. Subscribes cooperatively to MagicTaskbar.Shared's existing SharedRawMouseInputRouter (avoiding any
    ///    per-process RegisterRawInputDevices conflict with Dock/TopBar and adding zero WH_MOUSE_LL hooks) to
    ///    pre-grant AllowSetForegroundWindow and pre-focus DesktopHost's overlay window (HonorDesktop_HideWnd)
    ///    during WM_RBUTTONDOWN on a background worker, eliminating the synchronous focus-restore stall and
    ///    AllowSetForegroundWindow Win32 Error 5 on WM_RBUTTONUP.
    /// </summary>
    internal static class DesktopRightClickAccelerator
    {
        private const string DesktopHostProcessName = "DesktopHost";
        private const string MenuHostProcessName = "MenuHostNative";
        private const string CardMenuHostProcessName = "CardMenuHostNative";
        private const string DesktopOverlayClassName = "HonorDesktop_HideWnd_Class";
        private const string CardMountHostClassName = "NewExplorerCardMountHost";
        private const string MenuHostPipeName = @"\\.\pipe\NewExplorer.MenuHost.v2";
        private const string SharedRouterOwnerKey = "MagicToolbox.DesktopRightClickAccelerator";

        private const int WatcherIdlePollMs = 15000;
        private const int PostExitSettleMs = 220;
        private const int MinRelaunchIntervalMs = 3000;
        private const int RapidExitWindowMs = 2000;
        private const int MaxConsecutiveRapidExits = 3;

        private static readonly object LifecycleSync = new();
        private static readonly object BoostSync = new();
        private static readonly HashSet<int> BoostedPids = new();

        private static int _initialized;
        private static int _running;
        private static int _processExiting;

        private static Dispatcher? _uiDispatcher;
        private static ManualResetEvent? _stopEvent;
        private static Thread? _watcherThread;

        // SharedRawMouseInputRouter reflection cache (compiled getter avoids per-move reflection/boxing).
        private static MethodInfo? _routerRequestMethod;
        private static MethodInfo? _routerReleaseMethod;
        private static Delegate? _routerHandlerDelegate;
        private static Func<object, ushort>? _getButtonFlags;
        private static bool _routerSubscribed;

        // Cached runtime targets updated by the watcher thread (lock-free volatile reads on mouse path).
        private static int _cachedDesktopHostPid;
        private static int _cachedMenuHostPid;
        private static long _cachedOverlayHwndRaw;
        private static volatile string? _cachedMenuHostExePath;
        private static long _lastDesktopRButtonDownTick;

        public static void Initialize(Dispatcher? uiDispatcher = null)
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 1)
            {
                return;
            }

            _uiDispatcher = uiDispatcher ?? Application.Current?.Dispatcher;
            AdaptiveSettingsStore.FastDesktopRightClickChanged += OnSettingChanged;
            AdaptiveSettingsStore.NativeWindowsDesktopChanged += OnNativeDesktopChanged;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            try
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"DesktopRightClickAccelerator PowerModeChanged hook skipped: {ex.Message}");
            }

            bool shouldRun = ShouldRun();
            if (shouldRun)
            {
                Start();
            }

            DiagnosticLog.Info($"DesktopRightClickAccelerator initialized (enabled={shouldRun}, nativeDesktop={AdaptiveSettingsStore.IsNativeWindowsDesktopEnabled}).");
        }

        private static bool ShouldRun()
        {
            return AdaptiveSettingsStore.IsFastDesktopRightClickEnabled &&
                   !AdaptiveSettingsStore.IsNativeWindowsDesktopEnabled;
        }

        private static void OnSettingChanged(bool enabled)
        {
            if (Volatile.Read(ref _processExiting) == 1)
            {
                return;
            }

            if (ShouldRun())
            {
                Start();
            }
            else
            {
                Stop(restoreDefaults: !AdaptiveSettingsStore.IsNativeWindowsDesktopEnabled);
            }
        }

        private static void OnNativeDesktopChanged(bool nativeDesktopEnabled)
        {
            if (Volatile.Read(ref _processExiting) == 1)
            {
                return;
            }

            if (ShouldRun())
            {
                Start();
            }
            else
            {
                // When native desktop mode is active, DesktopHost is shutting down; skip touching its priority.
                Stop(restoreDefaults: !nativeDesktopEnabled);
            }
        }

        private static void OnProcessExit(object? sender, EventArgs e)
        {
            Volatile.Write(ref _processExiting, 1);
            try
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch
            {
            }

            Stop(restoreDefaults: false);
        }

        private static void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume ||
                Volatile.Read(ref _running) == 0 ||
                Volatile.Read(ref _processExiting) == 1 ||
                !ShouldRun())
            {
                return;
            }

            // Re-apply QoS/priority after resume because Windows can re-enable EcoQoS on background hosts.
            lock (BoostSync)
            {
                BoostedPids.Clear();
            }

            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try
                {
                    Thread.Sleep(1500);
                    if (Volatile.Read(ref _running) == 1 && Volatile.Read(ref _processExiting) == 0 && ShouldRun())
                    {
                        RefreshTargetsAndBoost(allowPrewarm: true);
                    }
                }
                catch
                {
                }
            }, null);
        }

        private static void Start()
        {
            lock (LifecycleSync)
            {
                if (Volatile.Read(ref _running) == 1 || Volatile.Read(ref _processExiting) == 1)
                {
                    return;
                }

                _stopEvent = new ManualResetEvent(false);
                Volatile.Write(ref _running, 1);

                _watcherThread = new Thread(WatcherLoop)
                {
                    Name = "DesktopRightClickWatcher",
                    IsBackground = true
                };
                _watcherThread.Start(_stopEvent);

                DispatchToUi(SubscribeSharedRawMouseInputOnUi);
                DiagnosticLog.Info("DesktopRightClickAccelerator started.");
            }
        }

        private static void Stop(bool restoreDefaults)
        {
            Thread? watcher;
            ManualResetEvent? stopEvt;

            lock (LifecycleSync)
            {
                if (Volatile.Read(ref _running) == 0)
                {
                    if (restoreDefaults)
                    {
                        RestoreProcessDefaults();
                    }
                    return;
                }

                Volatile.Write(ref _running, 0);
                stopEvt = _stopEvent;
                _stopEvent = null;
                watcher = _watcherThread;
                _watcherThread = null;
            }

            DispatchToUi(UnsubscribeSharedRawMouseInputOnUi);

            try
            {
                stopEvt?.Set();
            }
            catch
            {
            }

            try
            {
                watcher?.Join(1200);
            }
            catch
            {
            }

            try
            {
                stopEvt?.Dispose();
            }
            catch
            {
            }

            if (restoreDefaults)
            {
                RestoreProcessDefaults();
            }

            lock (BoostSync)
            {
                BoostedPids.Clear();
            }

            Volatile.Write(ref _cachedDesktopHostPid, 0);
            Volatile.Write(ref _cachedMenuHostPid, 0);
            Volatile.Write(ref _cachedOverlayHwndRaw, 0);
            Volatile.Write(ref _lastDesktopRButtonDownTick, 0);
            DiagnosticLog.Info($"DesktopRightClickAccelerator stopped (restoreDefaults={restoreDefaults}).");
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
                DiagnosticLog.Warn($"DesktopRightClickAccelerator UI dispatch skipped: {ex.Message}");
            }
        }

        private static void SubscribeSharedRawMouseInputOnUi()
        {
            if (Volatile.Read(ref _running) == 0 || _routerSubscribed)
            {
                return;
            }

            try
            {
                if (!EnsureSharedRouterBindings())
                {
                    DiagnosticLog.Warn("DesktopRightClickAccelerator: SharedRawMouseInputRouter unavailable; running boost/prewarm only.");
                    return;
                }

                object? res = _routerRequestMethod!.Invoke(
                    null,
                    new object[] { SharedRouterOwnerKey, _routerHandlerDelegate!, "desktop-rclick-prefocus" });
                _routerSubscribed = res is not bool b || b;
                DiagnosticLog.Info($"DesktopRightClickAccelerator subscribed to SharedRawMouseInputRouter (subscribed={_routerSubscribed}).");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"DesktopRightClickAccelerator SharedRawMouseInputRouter subscribe failed: {ex.Message}");
            }
        }

        private static void UnsubscribeSharedRawMouseInputOnUi()
        {
            if (!_routerSubscribed)
            {
                return;
            }

            _routerSubscribed = false;
            try
            {
                _routerReleaseMethod?.Invoke(null, new object[] { SharedRouterOwnerKey, "desktop-rclick-disabled" });
                DiagnosticLog.Info("DesktopRightClickAccelerator released SharedRawMouseInputRouter subscription.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"DesktopRightClickAccelerator SharedRawMouseInputRouter release failed: {ex.Message}");
            }
        }

        private static bool EnsureSharedRouterBindings()
        {
            if (_routerRequestMethod != null && _routerHandlerDelegate != null && _getButtonFlags != null)
            {
                return true;
            }

            Assembly? sharedAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "MagicTaskbar.Shared", StringComparison.OrdinalIgnoreCase));
            Type? routerType = sharedAsm?.GetType("MagicTaskbarWpf.SharedRawMouseInputRouter");
            Type? argsType = sharedAsm?.GetType("MagicTaskbarWpf.RawMouseInputEventArgs");
            if (routerType == null || argsType == null)
            {
                return false;
            }

            const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo? req = routerType.GetMethod("Request", staticFlags);
            MethodInfo? rel = routerType.GetMethod("Release", staticFlags);
            PropertyInfo? btnFlagsProp = argsType.GetProperty("ButtonFlags", BindingFlags.Public | BindingFlags.Instance);
            if (req == null || rel == null || btnFlagsProp?.GetMethod == null)
            {
                return false;
            }

            ParameterInfo[] reqParams = req.GetParameters();
            if (reqParams.Length != 3)
            {
                return false;
            }

            Type handlerType = reqParams[1].ParameterType;
            MethodInfo? invokeMethod = handlerType.GetMethod("Invoke");
            if (invokeMethod == null)
            {
                return false;
            }

            // Compile fast ButtonFlags getter: (object e) => ((RawMouseInputEventArgs)e).ButtonFlags
            ParameterExpression objArg = Expression.Parameter(typeof(object), "e");
            UnaryExpression typedArg = Expression.Convert(objArg, argsType);
            MemberExpression propAccess = Expression.Property(typedArg, btnFlagsProp);
            UnaryExpression toUShort = Expression.Convert(propAccess, typeof(ushort));
            _getButtonFlags = Expression.Lambda<Func<object, ushort>>(toUShort, objArg).Compile();

            // Build strongly typed delegate matching Request's handler parameter (1 or 2 parameters).
            ParameterInfo[] invokeParams = invokeMethod.GetParameters();
            MethodInfo targetDispatch = typeof(DesktopRightClickAccelerator).GetMethod(
                nameof(OnSharedRawMouseInput),
                BindingFlags.NonPublic | BindingFlags.Static)!;

            if (invokeParams.Length == 2)
            {
                ParameterExpression p0 = Expression.Parameter(invokeParams[0].ParameterType, "sender");
                ParameterExpression p1 = Expression.Parameter(invokeParams[1].ParameterType, "args");
                MethodCallExpression call = Expression.Call(targetDispatch, Expression.Convert(p1, typeof(object)));
                _routerHandlerDelegate = Expression.Lambda(handlerType, call, p0, p1).Compile();
            }
            else if (invokeParams.Length == 1)
            {
                ParameterExpression p0 = Expression.Parameter(invokeParams[0].ParameterType, "args");
                MethodCallExpression call = Expression.Call(targetDispatch, Expression.Convert(p0, typeof(object)));
                _routerHandlerDelegate = Expression.Lambda(handlerType, call, p0).Compile();
            }
            else
            {
                return false;
            }

            _routerRequestMethod = req;
            _routerReleaseMethod = rel;
            return true;
        }

        private static void OnSharedRawMouseInput(object? args)
        {
            if (args == null || Volatile.Read(ref _running) == 0)
            {
                return;
            }

            Func<object, ushort>? getter = _getButtonFlags;
            if (getter == null)
            {
                return;
            }

            // Hot path: mouse moves & left/middle clicks exit immediately in nanoseconds without touching Win32 or allocating.
            ushort flags = getter(args);
            bool rightDown = (flags & RiMouseRightButtonDown) != 0;
            bool rightUp = (flags & RiMouseRightButtonUp) != 0;
            if (!rightDown && !rightUp)
            {
                return;
            }

            if (Volatile.Read(ref _cachedDesktopHostPid) == 0)
            {
                return;
            }

            if (rightDown)
            {
                ThreadPool.UnsafeQueueUserWorkItem(_ => HandleRightButtonDown(), null);
            }

            if (rightUp)
            {
                ThreadPool.UnsafeQueueUserWorkItem(_ => HandleRightButtonUp(), null);
            }
        }

        private static void WatcherLoop(object? state)
        {
            if (state is not ManualResetEvent stopEvt)
            {
                return;
            }

            long lastRelaunchTick = 0;
            int consecutiveRapidExits = 0;

            try
            {
                if (stopEvt.WaitOne(800))
                {
                    return;
                }

                while (Volatile.Read(ref _running) == 1 && !stopEvt.WaitOne(0))
                {
                    try
                    {
                        RefreshTargetsAndBoost(allowPrewarm: false);

                        int desktopPid = Volatile.Read(ref _cachedDesktopHostPid);
                        int menuPid = Volatile.Read(ref _cachedMenuHostPid);

                        if (desktopPid == 0)
                        {
                            if (stopEvt.WaitOne(WatcherIdlePollMs))
                            {
                                break;
                            }
                            continue;
                        }

                        if (menuPid == 0)
                        {
                            TryPrewarmIfEligible(stopEvt, ref lastRelaunchTick, ref consecutiveRapidExits);
                            if (stopEvt.WaitOne(2000))
                            {
                                break;
                            }
                            continue;
                        }

                        // Event-driven wait on MenuHostNative process exit (0% idle CPU).
                        IntPtr hMenuProc = OpenProcess(ProcessSynchronize, false, (uint)menuPid);
                        if (hMenuProc == IntPtr.Zero)
                        {
                            if (stopEvt.WaitOne(3000))
                            {
                                break;
                            }
                            continue;
                        }

                        bool menuExited = false;
                        try
                        {
                            using var procWait = new ProcessHandleWaitHandle(hMenuProc);
                            int signaled = WaitHandle.WaitAny(new WaitHandle[] { stopEvt, procWait }, WatcherIdlePollMs);
                            if (signaled == 0)
                            {
                                break;
                            }

                            if (signaled == 1)
                            {
                                menuExited = true;
                            }
                        }
                        finally
                        {
                            CloseHandle(hMenuProc);
                        }

                        if (menuExited)
                        {
                            long now = Environment.TickCount64;
                            if (lastRelaunchTick != 0 && now - lastRelaunchTick < RapidExitWindowMs)
                            {
                                consecutiveRapidExits++;
                            }
                            else
                            {
                                consecutiveRapidExits = 0;
                            }

                            lock (BoostSync)
                            {
                                BoostedPids.Remove(menuPid);
                            }

                            Volatile.Write(ref _cachedMenuHostPid, 0);
                            DiagnosticLog.Info($"MenuHostNative exited (pid={menuPid}); checking whether DesktopHost is still active for prewarm.");

                            if (stopEvt.WaitOne(PostExitSettleMs))
                            {
                                break;
                            }

                            TryPrewarmIfEligible(stopEvt, ref lastRelaunchTick, ref consecutiveRapidExits);
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Warn($"DesktopRightClickAccelerator watcher iteration error: {ex.Message}");
                        if (stopEvt.WaitOne(5000))
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"DesktopRightClickAccelerator watcher thread exited: {ex.Message}");
            }
        }

        private static void TryPrewarmIfEligible(
            ManualResetEvent stopEvt,
            ref long lastRelaunchTick,
            ref int consecutiveRapidExits)
        {
            if (Volatile.Read(ref _running) == 0 || stopEvt.WaitOne(0) || !ShouldRun())
            {
                return;
            }

            if (consecutiveRapidExits >= MaxConsecutiveRapidExits)
            {
                return;
            }

            long now = Environment.TickCount64;
            if (lastRelaunchTick != 0 && now - lastRelaunchTick < MinRelaunchIntervalMs)
            {
                return;
            }

            RefreshTargetsAndBoost(allowPrewarm: false);
            int desktopPid = Volatile.Read(ref _cachedDesktopHostPid);
            int existingMenuPid = Volatile.Read(ref _cachedMenuHostPid);
            if (desktopPid == 0 || existingMenuPid != 0)
            {
                return;
            }

            if (WaitNamedPipeW(MenuHostPipeName, 0))
            {
                return;
            }

            IntPtr overlay = new IntPtr(Volatile.Read(ref _cachedOverlayHwndRaw));
            if (overlay == IntPtr.Zero || !IsWindow(overlay))
            {
                return;
            }

            string? menuHostPath = ResolveMenuHostExePath(desktopPid);
            if (string.IsNullOrEmpty(menuHostPath) || !File.Exists(menuHostPath))
            {
                return;
            }

            lastRelaunchTick = now;
            if (TryLaunchMenuHostAtDesktopHostPrivilege(desktopPid, menuHostPath, out uint launchedPid))
            {
                DiagnosticLog.Info($"Pre-warmed MenuHostNative at Medium IL (pid={launchedPid}) after recycle.");
                if (!stopEvt.WaitOne(250))
                {
                    RefreshTargetsAndBoost(allowPrewarm: false);
                }
            }
        }

        private static void RefreshTargetsAndBoost(bool allowPrewarm)
        {
            int desktopPid = FindFirstProcessId(DesktopHostProcessName);
            int menuPid = FindFirstProcessId(MenuHostProcessName);
            int cardMenuPid = FindFirstProcessId(CardMenuHostProcessName);

            Volatile.Write(ref _cachedDesktopHostPid, desktopPid);
            Volatile.Write(ref _cachedMenuHostPid, menuPid);

            if (desktopPid != 0)
            {
                IntPtr overlay = FindDesktopOverlayWindow(desktopPid);
                Volatile.Write(ref _cachedOverlayHwndRaw, overlay.ToInt64());

                if (string.IsNullOrEmpty(_cachedMenuHostExePath))
                {
                    _cachedMenuHostExePath = ResolveMenuHostExePath(desktopPid);
                }

                ApplyProcessBoostOnce(desktopPid, DesktopHostProcessName, AboveNormalPriorityClass);
            }
            else
            {
                Volatile.Write(ref _cachedOverlayHwndRaw, 0);
            }

            if (menuPid != 0)
            {
                if (string.IsNullOrEmpty(_cachedMenuHostExePath))
                {
                    string? livePath = TryQueryProcessImagePath(menuPid);
                    if (!string.IsNullOrEmpty(livePath))
                    {
                        _cachedMenuHostExePath = livePath;
                    }
                }

                ApplyProcessBoostOnce(menuPid, MenuHostProcessName, HighPriorityClass);
            }

            if (cardMenuPid != 0)
            {
                ApplyProcessBoostOnce(cardMenuPid, CardMenuHostProcessName, HighPriorityClass);
            }

            if (allowPrewarm && desktopPid != 0 && menuPid == 0)
            {
                string? exePath = ResolveMenuHostExePath(desktopPid);
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath) && !WaitNamedPipeW(MenuHostPipeName, 0))
                {
                    if (TryLaunchMenuHostAtDesktopHostPrivilege(desktopPid, exePath, out uint launchedPid))
                    {
                        Volatile.Write(ref _cachedMenuHostPid, (int)launchedPid);
                        ApplyProcessBoostOnce((int)launchedPid, MenuHostProcessName, HighPriorityClass);
                        DiagnosticLog.Info($"Post-resume pre-warmed MenuHostNative at Medium IL (pid={launchedPid}).");
                    }
                }
            }
        }

        private static void ApplyProcessBoostOnce(int pid, string processLabel, uint priorityClass)
        {
            lock (BoostSync)
            {
                if (!BoostedPids.Add(pid))
                {
                    return;
                }
            }

            IntPtr hProc = OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, (uint)pid);
            if (hProc == IntPtr.Zero)
            {
                lock (BoostSync)
                {
                    BoostedPids.Remove(pid);
                }
                return;
            }

            try
            {
                bool prioOk = SetPriorityClass(hProc, priorityClass);
                _ = SetProcessPriorityBoost(hProc, bDisablePriorityBoost: false);

                var throttling = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = ProcessPowerThrottlingCurrentVersion,
                    ControlMask = ProcessPowerThrottlingExecutionSpeed,
                    StateMask = 0 // Explicitly opt out of EcoQoS / Efficiency Mode
                };
                bool qosOk = SetProcessInformation(
                    hProc,
                    ProcessInformationClassPowerThrottling,
                    ref throttling,
                    (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());

                var memPrio = new MEMORY_PRIORITY_INFORMATION
                {
                    MemoryPriority = MemoryPriorityNormal
                };
                bool memOk = SetProcessInformation(
                    hProc,
                    ProcessInformationClassMemoryPriority,
                    ref memPrio,
                    (uint)Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>());

                DiagnosticLog.Info(
                    $"Boosted {processLabel} (pid={pid}): priority=0x{priorityClass:X}({prioOk}), antiEcoQoS={qosOk}, memNormal={memOk}.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"ApplyProcessBoostOnce failed for {processLabel} (pid={pid}): {ex.Message}");
            }
            finally
            {
                CloseHandle(hProc);
            }
        }

        private static void RestoreProcessDefaults()
        {
            foreach (string name in new[] { DesktopHostProcessName, MenuHostProcessName, CardMenuHostProcessName })
            {
                int pid = FindFirstProcessId(name);
                if (pid == 0)
                {
                    continue;
                }

                IntPtr hProc = OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, (uint)pid);
                if (hProc == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    _ = SetPriorityClass(hProc, NormalPriorityClass);
                    var defaultThrottling = new PROCESS_POWER_THROTTLING_STATE
                    {
                        Version = ProcessPowerThrottlingCurrentVersion,
                        ControlMask = 0,
                        StateMask = 0
                    };
                    _ = SetProcessInformation(
                        hProc,
                        ProcessInformationClassPowerThrottling,
                        ref defaultThrottling,
                        (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
                }
                catch
                {
                }
                finally
                {
                    CloseHandle(hProc);
                }
            }
        }

        private static string? ResolveMenuHostExePath(int desktopHostPid)
        {
            string? cached = _cachedMenuHostExePath;
            if (!string.IsNullOrEmpty(cached) && File.Exists(cached))
            {
                return cached;
            }

            string? desktopPath = TryQueryProcessImagePath(desktopHostPid);
            if (!string.IsNullOrEmpty(desktopPath))
            {
                string? dir = Path.GetDirectoryName(desktopPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    string candidate = Path.Combine(dir, "MenuHostNative", "MenuHostNative.exe");
                    if (File.Exists(candidate))
                    {
                        _cachedMenuHostExePath = candidate;
                        return candidate;
                    }
                }
            }

            const string defaultPath = @"C:\Program Files\HONOR\MagicAnimation\NewExplorer\MenuHostNative\MenuHostNative.exe";
            if (File.Exists(defaultPath))
            {
                _cachedMenuHostExePath = defaultPath;
                return defaultPath;
            }

            return null;
        }

        /// <summary>
        /// Launches MenuHostNative.exe using DesktopHost.exe's own Medium-IL access token so pipe permissions
        /// and UIPI message boundaries match official DesktopHost launches identically (never launches elevated).
        /// </summary>
        private static bool TryLaunchMenuHostAtDesktopHostPrivilege(int desktopHostPid, string exePath, out uint launchedPid)
        {
            launchedPid = 0;
            IntPtr hDesktopProc = IntPtr.Zero;
            IntPtr hToken = IntPtr.Zero;
            IntPtr hPrimaryToken = IntPtr.Zero;
            IntPtr envBlock = IntPtr.Zero;

            try
            {
                hDesktopProc = OpenProcess(ProcessQueryInformation, false, (uint)desktopHostPid);
                if (hDesktopProc == IntPtr.Zero)
                {
                    hDesktopProc = OpenProcess(ProcessQueryLimitedInformation, false, (uint)desktopHostPid);
                }

                if (hDesktopProc == IntPtr.Zero)
                {
                    DiagnosticLog.Warn($"Prewarm skipped: OpenProcess(DesktopHost pid={desktopHostPid}) failed err={Marshal.GetLastWin32Error()}.");
                    return false;
                }

                if (!OpenProcessToken(hDesktopProc, TokenDuplicate | TokenQuery, out hToken))
                {
                    DiagnosticLog.Warn($"Prewarm skipped: OpenProcessToken(DesktopHost) failed err={Marshal.GetLastWin32Error()}.");
                    return false;
                }

                if (!DuplicateTokenEx(
                    hToken,
                    TokenAssignPrimary | TokenDuplicate | TokenQuery | TokenAdjustDefault | TokenAdjustSessionId,
                    IntPtr.Zero,
                    SecurityImpersonation,
                    TokenTypePrimary,
                    out hPrimaryToken))
                {
                    DiagnosticLog.Warn($"Prewarm skipped: DuplicateTokenEx failed err={Marshal.GetLastWin32Error()}.");
                    return false;
                }

                bool hasEnv = CreateEnvironmentBlock(out envBlock, hPrimaryToken, bInherit: false);
                uint creationFlags = hasEnv ? CreateUnicodeEnvironment : 0u;
                string? workDir = Path.GetDirectoryName(exePath);
                var cmdLine = new StringBuilder($"\"{exePath}\"");

                var si = new STARTUPINFOW
                {
                    cb = Marshal.SizeOf<STARTUPINFOW>(),
                    lpDesktop = @"winsta0\default"
                };

                if (!CreateProcessWithTokenW(
                    hPrimaryToken,
                    0,
                    exePath,
                    cmdLine,
                    creationFlags,
                    hasEnv ? envBlock : IntPtr.Zero,
                    workDir,
                    ref si,
                    out PROCESS_INFORMATION pi))
                {
                    DiagnosticLog.Warn($"Prewarm skipped: CreateProcessWithTokenW(MenuHostNative) failed err={Marshal.GetLastWin32Error()}.");
                    return false;
                }

                launchedPid = pi.dwProcessId;
                if (pi.hThread != IntPtr.Zero)
                {
                    CloseHandle(pi.hThread);
                }
                if (pi.hProcess != IntPtr.Zero)
                {
                    CloseHandle(pi.hProcess);
                }

                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"TryLaunchMenuHostAtDesktopHostPrivilege exception: {ex.Message}");
                return false;
            }
            finally
            {
                if (envBlock != IntPtr.Zero)
                {
                    _ = DestroyEnvironmentBlock(envBlock);
                }
                if (hPrimaryToken != IntPtr.Zero)
                {
                    CloseHandle(hPrimaryToken);
                }
                if (hToken != IntPtr.Zero)
                {
                    CloseHandle(hToken);
                }
                if (hDesktopProc != IntPtr.Zero)
                {
                    CloseHandle(hDesktopProc);
                }
            }
        }

        private static void HandleRightButtonDown()
        {
            if (Volatile.Read(ref _running) == 0)
            {
                return;
            }

            int desktopPid = Volatile.Read(ref _cachedDesktopHostPid);
            if (desktopPid == 0 || !GetCursorPos(out POINT pt))
            {
                return;
            }

            IntPtr hit = WindowFromPoint(pt);
            if (hit == IntPtr.Zero)
            {
                return;
            }

            _ = GetWindowThreadProcessId(hit, out uint hitPid);
            if (hitPid != (uint)desktopPid)
            {
                return;
            }

            string hitCls = GetWindowClassName(hit);
            bool isOverlay = string.Equals(hitCls, DesktopOverlayClassName, StringComparison.Ordinal);
            bool isMountHost = !isOverlay && string.Equals(hitCls, CardMountHostClassName, StringComparison.Ordinal);
            if (!isOverlay && !isMountHost)
            {
                return;
            }

            IntPtr overlayHwnd = isOverlay ? hit : ResolveOverlayFromChild(hit);
            if (overlayHwnd == IntPtr.Zero || !IsWindow(overlayHwnd))
            {
                return;
            }

            Volatile.Write(ref _cachedOverlayHwndRaw, overlayHwnd.ToInt64());
            Volatile.Write(ref _lastDesktopRButtonDownTick, Environment.TickCount64);

            int menuPid = Volatile.Read(ref _cachedMenuHostPid);
            _ = AllowSetForegroundWindow((uint)desktopPid);
            if (menuPid != 0)
            {
                _ = AllowSetForegroundWindow((uint)menuPid);
            }

            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                _ = GetWindowThreadProcessId(fg, out uint fgPid);
                // Never steal focus from an already-visible MenuHostNative popup (preserves repeated-click recovery).
                if (menuPid != 0 && fgPid == (uint)menuPid)
                {
                    return;
                }

                // Never attach input queues to an unresponsive application window.
                if (IsHungAppWindow(fg))
                {
                    return;
                }
            }

            uint overlayThread = GetWindowThreadProcessId(overlayHwnd, out _);
            if (overlayThread == 0)
            {
                return;
            }

            // Fast check: if DesktopHost overlay already owns focus, DesktopHost!RestoreDesktopInputFocus
            // will already return in 0ms on WM_RBUTTONUP without touching AttachThreadInput.
            var gui = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(overlayThread, ref gui) && gui.hwndFocus == overlayHwnd)
            {
                return;
            }

            // Pre-focus DesktopHost overlay during WM_RBUTTONDOWN hold (~100ms before WM_RBUTTONUP).
            uint curThread = GetCurrentThreadId();
            uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
            bool attachedFg = false;
            bool attachedOverlay = false;

            try
            {
                if (fgThread != 0 && fgThread != curThread && fgThread != overlayThread)
                {
                    attachedFg = AttachThreadInput(curThread, fgThread, true);
                }

                if (overlayThread != curThread)
                {
                    attachedOverlay = AttachThreadInput(curThread, overlayThread, true);
                }

                _ = SetForegroundWindow(overlayHwnd);
                _ = SetActiveWindow(overlayHwnd);
                _ = SetFocus(overlayHwnd);
            }
            catch
            {
            }
            finally
            {
                if (attachedOverlay)
                {
                    _ = AttachThreadInput(curThread, overlayThread, false);
                }
                if (attachedFg)
                {
                    _ = AttachThreadInput(curThread, fgThread, false);
                }
            }

            _ = AllowSetForegroundWindow((uint)desktopPid);
            if (menuPid != 0)
            {
                _ = AllowSetForegroundWindow((uint)menuPid);
            }
        }

        private static void HandleRightButtonUp()
        {
            if (Volatile.Read(ref _running) == 0)
            {
                return;
            }

            long downTick = Volatile.Read(ref _lastDesktopRButtonDownTick);
            if (downTick == 0 || Environment.TickCount64 - downTick > 1500)
            {
                return;
            }

            Volatile.Write(ref _lastDesktopRButtonDownTick, 0);

            int desktopPid = Volatile.Read(ref _cachedDesktopHostPid);
            int menuPid = Volatile.Read(ref _cachedMenuHostPid);
            if (desktopPid != 0)
            {
                _ = AllowSetForegroundWindow((uint)desktopPid);
            }
            if (menuPid != 0)
            {
                _ = AllowSetForegroundWindow((uint)menuPid);
            }
        }

        private static IntPtr ResolveOverlayFromChild(IntPtr child)
        {
            IntPtr cur = child;
            for (int depth = 0; depth < 4 && cur != IntPtr.Zero; depth++)
            {
                IntPtr parent = GetParent(cur);
                if (parent == IntPtr.Zero)
                {
                    break;
                }

                if (string.Equals(GetWindowClassName(parent), DesktopOverlayClassName, StringComparison.Ordinal))
                {
                    return parent;
                }

                cur = parent;
            }

            IntPtr cached = new IntPtr(Volatile.Read(ref _cachedOverlayHwndRaw));
            return cached != IntPtr.Zero && IsWindow(cached) ? cached : IntPtr.Zero;
        }

        private static IntPtr FindDesktopOverlayWindow(int desktopHostPid)
        {
            IntPtr progman = FindWindowW("Progman", null);
            if (progman != IntPtr.Zero)
            {
                IntPtr child = FindWindowExW(progman, IntPtr.Zero, DesktopOverlayClassName, null);
                while (child != IntPtr.Zero)
                {
                    _ = GetWindowThreadProcessId(child, out uint pid);
                    if (pid == (uint)desktopHostPid)
                    {
                        return child;
                    }
                    child = FindWindowExW(progman, child, DesktopOverlayClassName, null);
                }
            }

            IntPtr top = FindWindowW(DesktopOverlayClassName, null);
            if (top != IntPtr.Zero)
            {
                _ = GetWindowThreadProcessId(top, out uint pid);
                if (pid == (uint)desktopHostPid)
                {
                    return top;
                }
            }

            return IntPtr.Zero;
        }

        private static int FindFirstProcessId(string processName)
        {
            Process[] procs = Array.Empty<Process>();
            try
            {
                procs = Process.GetProcessesByName(processName);
                return procs.Length > 0 ? procs[0].Id : 0;
            }
            catch
            {
                return 0;
            }
            finally
            {
                for (int i = 0; i < procs.Length; i++)
                {
                    try
                    {
                        procs[i].Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static string? TryQueryProcessImagePath(int pid)
        {
            IntPtr hProc = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (hProc == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var sb = new StringBuilder(512);
                uint len = (uint)sb.Capacity;
                return QueryFullProcessImageNameW(hProc, 0, sb, ref len) ? sb.ToString() : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                CloseHandle(hProc);
            }
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(128);
            int len = GetClassNameW(hwnd, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : string.Empty;
        }

        private sealed class ProcessHandleWaitHandle : WaitHandle
        {
            public ProcessHandleWaitHandle(IntPtr rawHandle)
            {
                SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(rawHandle, ownsHandle: false);
            }
        }

        #region Win32 Interop

        private const uint ProcessSynchronize = 0x00100000;
        private const uint ProcessSetInformation = 0x0200;
        private const uint ProcessQueryInformation = 0x0400;
        private const uint ProcessQueryLimitedInformation = 0x1000;

        private const uint NormalPriorityClass = 0x00000020;
        private const uint HighPriorityClass = 0x00000080;
        private const uint AboveNormalPriorityClass = 0x00008000;

        private const int ProcessInformationClassMemoryPriority = 0;
        private const int ProcessInformationClassPowerThrottling = 4;
        private const uint ProcessPowerThrottlingCurrentVersion = 1;
        private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;
        private const uint MemoryPriorityNormal = 5;

        private const uint TokenAssignPrimary = 0x0001;
        private const uint TokenDuplicate = 0x0002;
        private const uint TokenQuery = 0x0008;
        private const uint TokenAdjustDefault = 0x0080;
        private const uint TokenAdjustSessionId = 0x0100;
        private const int SecurityImpersonation = 2;
        private const int TokenTypePrimary = 1;
        private const uint CreateUnicodeEnvironment = 0x00000400;

        private const ushort RiMouseRightButtonDown = 0x0004;
        private const ushort RiMouseRightButtonUp = 0x0008;

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_PRIORITY_INFORMATION
        {
            public uint MemoryPriority;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOW
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

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
        private struct GUITHREADINFO
        {
            public uint cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessPriorityBoost(IntPtr hProcess, bool bDisablePriorityBoost);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(
            IntPtr hProcess,
            int processInformationClass,
            ref PROCESS_POWER_THROTTLING_STATE processInformation,
            uint processInformationSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(
            IntPtr hProcess,
            int processInformationClass,
            ref MEMORY_PRIORITY_INFORMATION processInformation,
            uint processInformationSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageNameW(
            IntPtr hProcess,
            uint dwFlags,
            StringBuilder lpExeName,
            ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool WaitNamedPipeW(string lpNamedPipeName, uint nTimeOut);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(
            IntPtr hExistingToken,
            uint dwDesiredAccess,
            IntPtr lpTokenAttributes,
            int impersonationLevel,
            int tokenType,
            out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessWithTokenW(
            IntPtr hToken,
            uint dwLogonFlags,
            string? lpApplicationName,
            StringBuilder? lpCommandLine,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            [In] ref STARTUPINFOW lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsHungAppWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(uint dwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowExW(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

        #endregion
    }
}

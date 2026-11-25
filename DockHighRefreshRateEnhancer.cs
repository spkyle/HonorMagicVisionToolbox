using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq.Expressions;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TopBarAdaptiveColor
{
    /// <summary>
    /// Unlocks true native monitor refresh rate (120Hz / 144Hz / 165Hz / 240Hz / 360Hz) on Honor Magic Vision's
    /// bottom Dock (MagicTaskbarWpf.MainWindow) without modifying any official binaries:
    /// - Honor's Dock motion physics (NormalizeFrameEasing) and DirectComposition backend (DockCompositionHost)
    ///   already support arbitrary delta-time frames, but MainWindow throttles mouse hover updates to 15.0 ms (~66 Hz),
    ///   blocks OnRendering for 20.0 ms after input, and relies on WPF CompositionTarget.Rendering which is capped at 60 FPS.
    /// - On Windows 11 laptops with Panel Self-Refresh (PSR), MPO, or Dynamic Refresh Rate, DwmFlush() frequently
    ///   skips idle/semi-idle composition passes (stalling 15-147 ms) instead of pacing cleanly at native refresh rate.
    /// - This enhancer paces a dedicated background thread directly to the Dock monitor's hardware VBlank via
    ///   IDXGIOutput::WaitForVBlank() (measured ±0.15 ms jitter at 120.0 Hz), with automatic fallback to a high-resolution
    ///   kernel waitable timer paced to the monitor's exact refresh rate and finally DwmFlush().
    /// - Zero CPU / zero wakeups when idle (_isRenderingActive == false).
    /// </summary>
    internal sealed class DockHighRefreshRateEnhancer : IDisposable
    {
        private const double MinFrameIntervalFloorMs = 1.5;
        private const double MinFrameIntervalCeilingMs = 4.6; // > 4.17ms prevents NormalizeFrameEasing 0.25x clamp double-step on hover entry while staying well below 8.33ms @ 120Hz
        private const int IdleGraceFlushes = 16;
        private const int MaxInitialAttachPolls = 30;
        private const int BurstSampleCapacity = 128;
        private const long TelemetryLogCooldownMs = 10000;
        private const int DxgiFastReturnThreshold = 4;
        private const long DxgiRetryBackoffMs = 15000;

        private const uint MonitorDefaultToNearest = 2;
        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAllAccess = 0x001F0003;
        private const int EnumCurrentSettings = -1;

        private static readonly string[] CompositionWakeEvents =
        {
            "HoverPositionChanged",
            "HoveredItemChanged",
            "PointerIntent",
            "FileDropIntent",
            "ItemInvoked",
            "ItemContextRequested"
        };

        private readonly Window _dockWindow;
        private readonly Action _tickAction;
        private readonly ManualResetEventSlim _wakeEvent = new(false);
        private readonly DispatcherTimer _attachPollTimer;

        // Compiled fast getters & invokers for MainWindow hot path (zero reflection/allocation per VBlank).
        private readonly Func<object, bool>? _getIsRenderingActive;
        private readonly Func<object, bool>? _getDockWindowClosed;
        private readonly Func<object, bool>? _getCompEnabled;
        private readonly Func<object, object?>? _getCompHost;
        private readonly Func<object, bool>? _isDragActive;
        private readonly Func<object, long, bool, double, bool, bool>? _tryUpdateMotion;
        private readonly FieldInfo? _compositionNativeWindowField;
        private readonly Func<object, int>? _getDockMinBackdropRefreshVersion;
        private readonly Action<object, int>? _setDockMinBackdropRefreshVersion;

        private Type? _compHostCachedType;
        private Func<object, bool>? _getCompHostIsAttached;

        private object? _hookedCompositionWindow;
        private readonly List<(EventInfo Event, Delegate Handler)> _hookedEvents = new();

        private Thread? _vsyncThread;
        private int _initialAttachPolls;
        private int _frameDispatchPending;
        private int _pendingBurstGapReset;
        private int _lastSuppressedMinRefreshVersion;
        private long _dockHwndSnapshot;
        private int _displayTopologyVersion;
        private int _activeRefreshHz = 120;
        private volatile string _activePacerSource = "init";
        private volatile bool _disposed;

        /// <summary>
        /// True while the bottom Dock is actively animating at high refresh rate so other modules
        /// (such as AdaptiveTopBarController periodic BitBlt polling) avoid triggering global DWM GPU readbacks.
        /// </summary>
        internal static volatile bool IsDockMotionActive;

        // Lightweight UI-thread-only burst telemetry (pre-allocated, 0 allocations per frame).
        private readonly double[] _burstIntervalsMs = new double[BurstSampleCapacity];
        private readonly double[] _burstSortScratch = new double[BurstSampleCapacity];
        private int _burstSampleCount;
        private int _burstUpdatedFrames;
        private int _burstSkippedFrames;
        private long _burstStartTicks;
        private long _burstLastUpdatedTicks;
        private double _burstUiWorkSumMs;
        private double _burstUiWorkMaxMs;
        private long _lastTelemetryLogTickMs;

        public DockHighRefreshRateEnhancer(Window dockWindow)
        {
            _dockWindow = dockWindow ?? throw new ArgumentNullException(nameof(dockWindow));
            _tickAction = OnVsyncTickOnUi;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type mwType = dockWindow.GetType();

            _compositionNativeWindowField = mwType.GetField("_compositionNativeWindow", flags);
            _getDockMinBackdropRefreshVersion = CompileIntFieldGetter(mwType, "_dockMinimizedBackdropRefreshVersion", flags);
            _setDockMinBackdropRefreshVersion = CompileIntFieldSetter(mwType, "_dockMinimizedBackdropRefreshVersion", flags);
            _getIsRenderingActive = CompileBoolFieldGetter(mwType, "_isRenderingActive", flags);
            _getDockWindowClosed = CompileBoolFieldGetter(mwType, "_dockWindowClosed", flags);
            _getCompEnabled = CompileBoolFieldGetter(mwType, "_compositionNativeWindowEnabled", flags);
            _getCompHost = CompileObjectFieldGetter(mwType, "_compositionHost", flags);
            _isDragActive = CompileParameterlessBoolMethod(mwType, "IsDockPinDragInteractionActive", flags);
            _tryUpdateMotion = CompileTryUpdateMotionMethod(mwType, flags);

            _attachPollTimer = new DispatcherTimer(DispatcherPriority.Background, dockWindow.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _attachPollTimer.Tick += OnAttachPollTimerTick;

            if (_getIsRenderingActive == null || _getCompEnabled == null || _getCompHost == null || _tryUpdateMotion == null)
            {
                DiagnosticLog.Warn(
                    $"DockHighRefreshRateEnhancer: missing MainWindow bindings " +
                    $"(render={_getIsRenderingActive != null}, compEn={_getCompEnabled != null}, " +
                    $"compHost={_getCompHost != null}, tryUpdate={_tryUpdateMotion != null}); inactive.");
                return;
            }

            _dockWindow.Loaded += OnDockLifecycleWake;
            _dockWindow.IsVisibleChanged += OnDockVisibleChanged;
            _dockWindow.LocationChanged += OnDockGeometryWake;
            _dockWindow.SizeChanged += OnDockSizeWake;
            _dockWindow.LayoutUpdated += OnDockLayoutUpdated;
            _dockWindow.Closed += OnDockClosed;
            AdaptiveSettingsStore.DockHighRefreshRateChanged += OnSettingChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplayTopologyChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            if (!TryEnsureCompositionHooks())
            {
                _attachPollTimer.Start();
            }

            RefreshDockHwndSnapshot();

            _vsyncThread = new Thread(VsyncLoop)
            {
                Name = "DockHighRefreshVsyncPump",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _vsyncThread.Start();

            if (AdaptiveSettingsStore.IsDockHighRefreshRateEnabled)
            {
                SignalWake();
            }

            DiagnosticLog.Info(
                $"DockHighRefreshRateEnhancer attached (enabled={AdaptiveSettingsStore.IsDockHighRefreshRateEnabled}, " +
                $"compHooked={_hookedCompositionWindow != null}, minIntervalMs={ComputeMinFrameIntervalMs():F2}).");
        }

        private double ComputeMinFrameIntervalMs()
        {
            int hz = Math.Clamp(Volatile.Read(ref _activeRefreshHz), 60, 480);
            return Math.Clamp((1000.0 / hz) * 0.55, MinFrameIntervalFloorMs, MinFrameIntervalCeilingMs);
        }

        private void OnSettingChanged(bool enabled)
        {
            if (_disposed)
            {
                return;
            }

            if (enabled)
            {
                SignalWake();
            }
        }

        private void OnDisplayTopologyChanged(object? sender, EventArgs e)
        {
            if (!_disposed)
            {
                Interlocked.Increment(ref _displayTopologyVersion);
            }
        }

        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (!_disposed && e.Mode == PowerModes.Resume)
            {
                Interlocked.Increment(ref _displayTopologyVersion);
            }
        }

        private void OnAttachPollTimerTick(object? sender, EventArgs e)
        {
            if (_disposed)
            {
                _attachPollTimer.Stop();
                return;
            }

            _initialAttachPolls++;
            if (TryEnsureCompositionHooks() || _initialAttachPolls >= MaxInitialAttachPolls)
            {
                _attachPollTimer.Stop();
            }
        }

        private void OnDockLifecycleWake(object sender, RoutedEventArgs e)
        {
            if (!_disposed)
            {
                TryEnsureCompositionHooks();
                RefreshDockHwndSnapshot();
                SignalWake();
            }
        }

        private void OnDockVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!_disposed && e.NewValue is true)
            {
                TryEnsureCompositionHooks();
                RefreshDockHwndSnapshot();
                SignalWake();
            }
        }

        private void OnDockGeometryWake(object? sender, EventArgs e)
        {
            if (!_disposed)
            {
                RefreshDockHwndSnapshot();
                if (AdaptiveSettingsStore.IsDockHighRefreshRateEnabled)
                {
                    SignalWake();
                }
            }
        }

        private void OnDockSizeWake(object sender, SizeChangedEventArgs e)
        {
            if (!_disposed)
            {
                RefreshDockHwndSnapshot();
                if (AdaptiveSettingsStore.IsDockHighRefreshRateEnabled)
                {
                    SignalWake();
                }
            }
        }

        private void OnDockLayoutUpdated(object? sender, EventArgs e)
        {
            if (_disposed || _compositionNativeWindowField == null)
            {
                return;
            }

            object? current = _compositionNativeWindowField.GetValue(_dockWindow);
            if (current != null && !ReferenceEquals(current, _hookedCompositionWindow))
            {
                TryEnsureCompositionHooks();
            }

            if (AdaptiveSettingsStore.IsDockHighRefreshRateEnabled &&
                _getIsRenderingActive != null &&
                _getIsRenderingActive(_dockWindow))
            {
                SignalWake();
            }
        }

        private void RefreshDockHwndSnapshot()
        {
            try
            {
                IntPtr hwnd = IntPtr.Zero;
                object? ncdw = _hookedCompositionWindow ?? _compositionNativeWindowField?.GetValue(_dockWindow);
                if (ncdw != null)
                {
                    PropertyInfo? handleProp = ncdw.GetType().GetProperty(
                        "Handle",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (handleProp?.GetValue(ncdw) is IntPtr compHwnd && compHwnd != IntPtr.Zero)
                    {
                        hwnd = compHwnd;
                    }
                }

                if (hwnd == IntPtr.Zero)
                {
                    hwnd = new WindowInteropHelper(_dockWindow).Handle;
                }

                if (hwnd != IntPtr.Zero)
                {
                    Interlocked.Exchange(ref _dockHwndSnapshot, hwnd.ToInt64());
                }
            }
            catch
            {
            }
        }

        private bool TryEnsureCompositionHooks()
        {
            if (_disposed || _compositionNativeWindowField == null)
            {
                return false;
            }

            try
            {
                object? ncdw = _compositionNativeWindowField.GetValue(_dockWindow);
                if (ncdw == null)
                {
                    return false;
                }

                if (ReferenceEquals(ncdw, _hookedCompositionWindow))
                {
                    return true;
                }

                UnhookCompositionWindow();

                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                Type ncdwType = ncdw.GetType();
                int hookedCount = 0;

                foreach (string eventName in CompositionWakeEvents)
                {
                    EventInfo? evt = ncdwType.GetEvent(eventName, flags);
                    MethodInfo? addMethod = evt?.GetAddMethod(nonPublic: true);
                    if (evt?.EventHandlerType == null || addMethod == null)
                    {
                        continue;
                    }

                    Delegate? wakeDelegate = BuildWakeDelegate(evt.EventHandlerType);
                    if (wakeDelegate == null)
                    {
                        continue;
                    }

                    addMethod.Invoke(ncdw, new object[] { wakeDelegate });
                    _hookedEvents.Add((evt, wakeDelegate));
                    hookedCount++;
                }

                if (hookedCount > 0)
                {
                    _hookedCompositionWindow = ncdw;
                    RefreshDockHwndSnapshot();
                    DiagnosticLog.Info($"DockHighRefreshRateEnhancer hooked {hookedCount} NativeCompositionDockWindow wake events.");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"DockHighRefreshRateEnhancer hook failed: {ex.Message}");
                return false;
            }
        }

        private Delegate? BuildWakeDelegate(Type handlerType)
        {
            MethodInfo? invoke = handlerType.GetMethod("Invoke");
            if (invoke == null || invoke.ReturnType != typeof(void))
            {
                return null;
            }

            ParameterInfo[] ps = invoke.GetParameters();
            var paramExprs = new ParameterExpression[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                paramExprs[i] = Expression.Parameter(ps[i].ParameterType, "p" + i);
            }

            MethodInfo wakeMethod = typeof(DockHighRefreshRateEnhancer).GetMethod(
                nameof(SignalWake),
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodCallExpression call = Expression.Call(Expression.Constant(this), wakeMethod);
            return Expression.Lambda(handlerType, call, paramExprs).Compile();
        }

        private void UnhookCompositionWindow()
        {
            object? ncdw = _hookedCompositionWindow;
            if (ncdw != null)
            {
                foreach (var (evt, handler) in _hookedEvents)
                {
                    try
                    {
                        evt.GetRemoveMethod(nonPublic: true)?.Invoke(ncdw, new object[] { handler });
                    }
                    catch
                    {
                    }
                }
            }

            _hookedEvents.Clear();
            _hookedCompositionWindow = null;
        }

        private void SignalWake()
        {
            if (!_disposed && AdaptiveSettingsStore.IsDockHighRefreshRateEnabled)
            {
                try
                {
                    _wakeEvent.Set();
                }
                catch
                {
                }
            }
        }

        private bool ShouldPumpFastCheck()
        {
            if (_disposed || !AdaptiveSettingsStore.IsDockHighRefreshRateEnabled)
            {
                return false;
            }

            if (_getDockWindowClosed != null && _getDockWindowClosed(_dockWindow))
            {
                return false;
            }

            return _getIsRenderingActive!(_dockWindow) &&
                   _getCompEnabled!(_dockWindow) &&
                   _getCompHost!(_dockWindow) != null;
        }

        private void VsyncLoop()
        {
            IntPtr dxgiOutput = IntPtr.Zero;
            WaitForVBlankDel? waitForVBlank = null;
            ReleaseComDel? releaseOutput = null;
            IntPtr cachedMonitor = IntPtr.Zero;
            int cachedTopologyVersion = -1;
            long dxgiDisabledUntilTickMs = 0;
            int dxgiConsecutiveFastReturns = 0;
            bool loggedInitialPacer = false;

            IntPtr hHighResTimer = CreateWaitableTimerExW(
                IntPtr.Zero,
                null,
                CreateWaitableTimerHighResolution,
                TimerAllAccess);
            double nextTimerDeadlineMs = 0;
            long timerOriginTicks = Stopwatch.GetTimestamp();

            try
            {
                while (!_disposed)
                {
                    _wakeEvent.Wait();
                    if (_disposed)
                    {
                        break;
                    }

                    if (!AdaptiveSettingsStore.IsDockHighRefreshRateEnabled)
                    {
                        _wakeEvent.Reset();
                        continue;
                    }

                    int consecutiveIdleFlushes = 0;
                    nextTimerDeadlineMs = 0;

                    while (!_disposed && AdaptiveSettingsStore.IsDockHighRefreshRateEnabled)
                    {
                        if (!ShouldPumpFastCheck())
                        {
                            IsDockMotionActive = false;
                            Interlocked.Exchange(ref _pendingBurstGapReset, 1);
                            consecutiveIdleFlushes++;
                            if (consecutiveIdleFlushes >= IdleGraceFlushes)
                            {
                                FlushBurstTelemetryAsync();
                                _wakeEvent.Reset();
                                // Double-check after Reset so a concurrent SignalWake() cannot be missed.
                                if (!ShouldPumpFastCheck())
                                {
                                    break;
                                }
                                consecutiveIdleFlushes = 0;
                                _wakeEvent.Set();
                            }
                        }
                        else
                        {
                            IsDockMotionActive = true;
                            consecutiveIdleFlushes = 0;
                        }

                        // Resolve current Dock monitor and keep DXGI hardware VBlank handle locked to it.
                        IntPtr dockHwnd = new IntPtr(Interlocked.Read(ref _dockHwndSnapshot));
                        IntPtr currentMonitor = dockHwnd != IntPtr.Zero
                            ? MonitorFromWindow(dockHwnd, MonitorDefaultToNearest)
                            : IntPtr.Zero;
                        int topologyVersion = Volatile.Read(ref _displayTopologyVersion);
                        long nowTickMs = Environment.TickCount64;

                        bool needReacquire =
                            topologyVersion != cachedTopologyVersion ||
                            (currentMonitor != IntPtr.Zero && currentMonitor != cachedMonitor) ||
                            (dxgiOutput == IntPtr.Zero && nowTickMs >= dxgiDisabledUntilTickMs);

                        if (needReacquire)
                        {
                            ReleaseDxgiOutput(ref dxgiOutput, ref waitForVBlank, ref releaseOutput);
                            cachedTopologyVersion = topologyVersion;
                            cachedMonitor = currentMonitor;

                            int hz = QueryMonitorRefreshRateHz(currentMonitor);
                            Volatile.Write(ref _activeRefreshHz, hz);

                            if (nowTickMs >= dxgiDisabledUntilTickMs &&
                                TryAcquireDxgiOutput(currentMonitor, out dxgiOutput, out waitForVBlank, out releaseOutput, out string matchedDevice))
                            {
                                dxgiConsecutiveFastReturns = 0;
                                _activePacerSource = "DXGI-VBlank";
                                if (!loggedInitialPacer)
                                {
                                    loggedInitialPacer = true;
                                    DiagnosticLog.Info(
                                        $"DockHighRefreshRateEnhancer pacer active: DXGI-VBlank (output='{matchedDevice}', monitor={hz}Hz).");
                                }
                            }
                            else if (hHighResTimer != IntPtr.Zero)
                            {
                                _activePacerSource = "HighResTimer";
                                if (!loggedInitialPacer)
                                {
                                    loggedInitialPacer = true;
                                    DiagnosticLog.Info(
                                        $"DockHighRefreshRateEnhancer pacer active: HighResTimer (monitor={hz}Hz).");
                                }
                            }
                            else
                            {
                                _activePacerSource = "DwmFlush";
                                if (!loggedInitialPacer)
                                {
                                    loggedInitialPacer = true;
                                    DiagnosticLog.Info(
                                        $"DockHighRefreshRateEnhancer pacer active: DwmFlush fallback (monitor={hz}Hz).");
                                }
                            }
                        }

                        // Wait for exact next hardware VBlank (or high-res timer / DwmFlush fallback).
                        bool waited = false;
                        if (dxgiOutput != IntPtr.Zero && waitForVBlank != null)
                        {
                            long t0 = Stopwatch.GetTimestamp();
                            int vhr = waitForVBlank(dxgiOutput);
                            double waitMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

                            if (vhr == 0)
                            {
                                if (waitMs < 1.0)
                                {
                                    dxgiConsecutiveFastReturns++;
                                    if (dxgiConsecutiveFastReturns >= DxgiFastReturnThreshold)
                                    {
                                        // Virtual/RDP display adapter returned without blocking; switch to HighResTimer cleanly.
                                        DiagnosticLog.Warn(
                                            $"DockHighRefreshRateEnhancer: DXGI WaitForVBlank non-blocking ({waitMs:F2}ms); switching to fallback.");
                                        ReleaseDxgiOutput(ref dxgiOutput, ref waitForVBlank, ref releaseOutput);
                                        dxgiDisabledUntilTickMs = Environment.TickCount64 + DxgiRetryBackoffMs;
                                        _activePacerSource = hHighResTimer != IntPtr.Zero ? "HighResTimer" : "DwmFlush";
                                    }
                                    else
                                    {
                                        waited = true;
                                    }
                                }
                                else
                                {
                                    dxgiConsecutiveFastReturns = 0;
                                    waited = true;
                                }
                            }
                            else
                            {
                                // Display mode changed / GPU reset / sleep-wake; drop handle and retry next cycle.
                                ReleaseDxgiOutput(ref dxgiOutput, ref waitForVBlank, ref releaseOutput);
                                dxgiDisabledUntilTickMs = Environment.TickCount64 + 1000;
                                _activePacerSource = hHighResTimer != IntPtr.Zero ? "HighResTimer" : "DwmFlush";
                            }
                        }

                        if (!waited)
                        {
                            if (hHighResTimer != IntPtr.Zero)
                            {
                                int hz = Math.Clamp(Volatile.Read(ref _activeRefreshHz), 60, 480);
                                double periodMs = 1000.0 / hz;
                                double nowRelMs = (Stopwatch.GetTimestamp() - timerOriginTicks) * 1000.0 / Stopwatch.Frequency;
                                if (nextTimerDeadlineMs <= nowRelMs || nextTimerDeadlineMs - nowRelMs > periodMs * 2.0)
                                {
                                    nextTimerDeadlineMs = nowRelMs + periodMs;
                                }

                                double remMs = nextTimerDeadlineMs - nowRelMs;
                                if (remMs > 0.25)
                                {
                                    long due100Ns = -(long)(remMs * 10000.0);
                                    if (SetWaitableTimerEx(hHighResTimer, ref due100Ns, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                                    {
                                        WaitForSingleObject(hHighResTimer, 50);
                                    }
                                    else
                                    {
                                        Thread.Sleep(Math.Max(1, (int)remMs));
                                    }
                                }

                                nextTimerDeadlineMs += periodMs;
                            }
                            else
                            {
                                int hr = DwmFlush();
                                if (hr != 0)
                                {
                                    Thread.Sleep(8);
                                    continue;
                                }
                            }
                        }

                        if (_disposed || !AdaptiveSettingsStore.IsDockHighRefreshRateEnabled)
                        {
                            break;
                        }

                        if (!ShouldPumpFastCheck())
                        {
                            continue;
                        }

                        Dispatcher disp = _dockWindow.Dispatcher;
                        if (disp.HasShutdownStarted || disp.HasShutdownFinished)
                        {
                            break;
                        }

                        if (Interlocked.CompareExchange(ref _frameDispatchPending, 1, 0) == 0)
                        {
                            try
                            {
                                disp.BeginInvoke(DispatcherPriority.Send, _tickAction);
                            }
                            catch
                            {
                                Interlocked.Exchange(ref _frameDispatchPending, 0);
                                break;
                            }
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"DockHighRefreshRateEnhancer VSync thread exited: {ex.Message}");
            }
            finally
            {
                IsDockMotionActive = false;
                ReleaseDxgiOutput(ref dxgiOutput, ref waitForVBlank, ref releaseOutput);
                if (hHighResTimer != IntPtr.Zero)
                {
                    CloseHandle(hHighResTimer);
                }
            }
        }

        private void OnVsyncTickOnUi()
        {
            Interlocked.Exchange(ref _frameDispatchPending, 0);

            if (_disposed || !AdaptiveSettingsStore.IsDockHighRefreshRateEnabled)
            {
                return;
            }

            try
            {
                if ((_getDockWindowClosed != null && _getDockWindowClosed(_dockWindow)) ||
                    !_getIsRenderingActive!(_dockWindow) ||
                    !_getCompEnabled!(_dockWindow))
                {
                    return;
                }

                object? compHost = _getCompHost!(_dockWindow);
                if (compHost == null)
                {
                    return;
                }

                Func<object, bool>? isAttached = EnsureCompHostIsAttachedGetter(compHost);
                if (isAttached != null && !isAttached(compHost))
                {
                    return;
                }

                long now = Stopwatch.GetTimestamp();
                bool dragActive = _isDragActive != null && _isDragActive(_dockWindow);

                // Bypass the 15ms mouse-move throttle while keeping inputDriven=true so WPF's 60Hz OnRendering
                // automatically yields via its 20ms sinceInputMs check (zero double-stepping).
                // Adaptive minInterval (~4.58ms @ 120Hz) prevents NormalizeFrameEasing's 0.25x clamp double-step on hover enter.
                double minIntervalMs = ComputeMinFrameIntervalMs();
                bool updated = _tryUpdateMotion!(_dockWindow, now, dragActive, minIntervalMs, true);
                double workMs = (Stopwatch.GetTimestamp() - now) * 1000.0 / Stopwatch.Frequency;

                // While Dock is actively animating at high refresh rate, cancel any pending minimized-window
                // RefreshBackdropSampling() callback (+1px/-1px HWND nudge + RedrawWindow + synchronous DwmFlush).
                if (updated && _getDockMinBackdropRefreshVersion != null && _setDockMinBackdropRefreshVersion != null)
                {
                    int cur = _getDockMinBackdropRefreshVersion(_dockWindow);
                    if (cur != 0 && cur != _lastSuppressedMinRefreshVersion)
                    {
                        int next = unchecked(cur + 1);
                        _setDockMinBackdropRefreshVersion(_dockWindow, next);
                        _lastSuppressedMinRefreshVersion = next;
                    }
                }

                RecordFrameTelemetry(now, updated, workMs);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"DockHighRefreshRateEnhancer UI tick failed: {ex.Message}");
            }
        }

        private void RecordFrameTelemetry(long nowTicks, bool updated, double workMs)
        {
            if (Interlocked.Exchange(ref _pendingBurstGapReset, 0) == 1)
            {
                // Motion paused briefly during idle grace; don't count the idle gap as a dropped frame interval.
                _burstLastUpdatedTicks = 0;
            }

            if (updated)
            {
                if (_burstStartTicks == 0)
                {
                    _burstStartTicks = nowTicks;
                }
                else if (_burstLastUpdatedTicks != 0)
                {
                    double dtMs = (nowTicks - _burstLastUpdatedTicks) * 1000.0 / Stopwatch.Frequency;
                    if (_burstSampleCount < BurstSampleCapacity)
                    {
                        _burstIntervalsMs[_burstSampleCount++] = dtMs;
                    }
                }

                _burstLastUpdatedTicks = nowTicks;
                _burstUpdatedFrames++;
                _burstUiWorkSumMs += workMs;
                if (workMs > _burstUiWorkMaxMs)
                {
                    _burstUiWorkMaxMs = workMs;
                }

                // Also flush periodically during sustained swipes so continuous hover still records metrics.
                if (_burstUpdatedFrames >= 120)
                {
                    EmitAndResetBurstTelemetryOnUi();
                }
            }
            else
            {
                _burstSkippedFrames++;
            }
        }

        private void FlushBurstTelemetryAsync()
        {
            try
            {
                Dispatcher disp = _dockWindow.Dispatcher;
                if (!_disposed && !disp.HasShutdownStarted && !disp.HasShutdownFinished)
                {
                    disp.BeginInvoke(DispatcherPriority.Background, new Action(EmitAndResetBurstTelemetryOnUi));
                }
            }
            catch
            {
            }
        }

        private void EmitAndResetBurstTelemetryOnUi()
        {
            int updated = _burstUpdatedFrames;
            int skipped = _burstSkippedFrames;
            int sampleCount = _burstSampleCount;
            long startTicks = _burstStartTicks;
            long endTicks = _burstLastUpdatedTicks;
            double uiSum = _burstUiWorkSumMs;
            double uiMax = _burstUiWorkMaxMs;

            _burstUpdatedFrames = 0;
            _burstSkippedFrames = 0;
            _burstSampleCount = 0;
            _burstStartTicks = 0;
            _burstLastUpdatedTicks = 0;
            _burstUiWorkSumMs = 0;
            _burstUiWorkMaxMs = 0;

            if (updated < 10 || startTicks == 0 || endTicks <= startTicks)
            {
                return;
            }

            long nowMs = Environment.TickCount64;
            if (nowMs - _lastTelemetryLogTickMs < TelemetryLogCooldownMs)
            {
                return;
            }

            _lastTelemetryLogTickMs = nowMs;

            double elapsedMs = (endTicks - startTicks) * 1000.0 / Stopwatch.Frequency;
            double fps = elapsedMs > 1.0 ? (updated - 1) * 1000.0 / elapsedMs : 0.0;
            double uiAvg = uiSum / updated;

            double p50 = 0;
            double p95 = 0;
            double maxDt = 0;
            if (sampleCount > 0)
            {
                Array.Copy(_burstIntervalsMs, _burstSortScratch, sampleCount);
                Array.Sort(_burstSortScratch, 0, sampleCount);
                p50 = _burstSortScratch[sampleCount / 2];
                p95 = _burstSortScratch[Math.Min(sampleCount - 1, (int)(sampleCount * 0.95))];
                maxDt = _burstSortScratch[sampleCount - 1];
            }

            DiagnosticLog.Info(
                $"DockHighRefreshRateEnhancer burst (pacer={_activePacerSource}, monitor={Volatile.Read(ref _activeRefreshHz)}Hz): " +
                $"frames={updated} ({fps:F1} fps over {elapsedMs:F0}ms), " +
                $"dtMs[p50={p50:F2}, p95={p95:F2}, max={maxDt:F2}], " +
                $"uiMs[avg={uiAvg:F2}, max={uiMax:F2}], skipped={skipped}.");
        }

        private Func<object, bool>? EnsureCompHostIsAttachedGetter(object compHost)
        {
            Type t = compHost.GetType();
            if (_compHostCachedType == t && _getCompHostIsAttached != null)
            {
                return _getCompHostIsAttached;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo? prop = t.GetProperty("IsAttached", flags);
            if (prop?.GetMethod == null || prop.PropertyType != typeof(bool))
            {
                _compHostCachedType = t;
                _getCompHostIsAttached = null;
                return null;
            }

            ParameterExpression objArg = Expression.Parameter(typeof(object), "host");
            MethodCallExpression call = Expression.Call(Expression.Convert(objArg, t), prop.GetMethod);
            _getCompHostIsAttached = Expression.Lambda<Func<object, bool>>(call, objArg).Compile();
            _compHostCachedType = t;
            return _getCompHostIsAttached;
        }

        private static bool TryAcquireDxgiOutput(
            IntPtr preferredMonitor,
            out IntPtr matchedOutput,
            out WaitForVBlankDel? waitForVBlank,
            out ReleaseComDel? releaseOutput,
            out string matchedDeviceName)
        {
            matchedOutput = IntPtr.Zero;
            waitForVBlank = null;
            releaseOutput = null;
            matchedDeviceName = string.Empty;

            Guid iidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
            IntPtr factory = IntPtr.Zero;
            IntPtr fallbackOutput = IntPtr.Zero;
            string fallbackDevice = string.Empty;

            try
            {
                if (CreateDXGIFactory1(ref iidFactory1, out factory) != 0 || factory == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr fvt = Marshal.ReadIntPtr(factory);
                var enumAdapters1 = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Del>(
                    Marshal.ReadIntPtr(fvt, 12 * IntPtr.Size));

                for (uint ai = 0; ai < 16; ai++)
                {
                    if (enumAdapters1(factory, ai, out IntPtr adapter) != 0 || adapter == IntPtr.Zero)
                    {
                        break;
                    }

                    try
                    {
                        IntPtr avt = Marshal.ReadIntPtr(adapter);
                        var enumOutputs = Marshal.GetDelegateForFunctionPointer<EnumOutputsDel>(
                            Marshal.ReadIntPtr(avt, 7 * IntPtr.Size));

                        for (uint oi = 0; oi < 16; oi++)
                        {
                            if (enumOutputs(adapter, oi, out IntPtr output) != 0 || output == IntPtr.Zero)
                            {
                                break;
                            }

                            IntPtr ovt = Marshal.ReadIntPtr(output);
                            var getDesc = Marshal.GetDelegateForFunctionPointer<GetOutputDescDel>(
                                Marshal.ReadIntPtr(ovt, 7 * IntPtr.Size));

                            bool keepOutput = false;
                            if (getDesc(output, out DXGI_OUTPUT_DESC desc) == 0 && desc.AttachedToDesktop != 0)
                            {
                                if (preferredMonitor != IntPtr.Zero && desc.Monitor == preferredMonitor)
                                {
                                    matchedOutput = output;
                                    matchedDeviceName = desc.DeviceName ?? string.Empty;
                                    keepOutput = true;
                                }
                                else if (fallbackOutput == IntPtr.Zero)
                                {
                                    fallbackOutput = output;
                                    fallbackDevice = desc.DeviceName ?? string.Empty;
                                    keepOutput = true;
                                }
                            }

                            if (!keepOutput)
                            {
                                ReleaseComObject(output);
                            }

                            if (matchedOutput != IntPtr.Zero)
                            {
                                break;
                            }
                        }
                    }
                    finally
                    {
                        ReleaseComObject(adapter);
                    }

                    if (matchedOutput != IntPtr.Zero)
                    {
                        break;
                    }
                }

                if (matchedOutput != IntPtr.Zero)
                {
                    if (fallbackOutput != IntPtr.Zero)
                    {
                        ReleaseComObject(fallbackOutput);
                        fallbackOutput = IntPtr.Zero;
                    }
                }
                else if (fallbackOutput != IntPtr.Zero)
                {
                    matchedOutput = fallbackOutput;
                    matchedDeviceName = fallbackDevice;
                    fallbackOutput = IntPtr.Zero;
                }

                if (matchedOutput == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr matchedVtable = Marshal.ReadIntPtr(matchedOutput);
                waitForVBlank = Marshal.GetDelegateForFunctionPointer<WaitForVBlankDel>(
                    Marshal.ReadIntPtr(matchedVtable, 10 * IntPtr.Size));
                releaseOutput = Marshal.GetDelegateForFunctionPointer<ReleaseComDel>(
                    Marshal.ReadIntPtr(matchedVtable, 2 * IntPtr.Size));
                return true;
            }
            catch
            {
                if (fallbackOutput != IntPtr.Zero)
                {
                    ReleaseComObject(fallbackOutput);
                }
                if (matchedOutput != IntPtr.Zero)
                {
                    ReleaseComObject(matchedOutput);
                    matchedOutput = IntPtr.Zero;
                }
                waitForVBlank = null;
                releaseOutput = null;
                return false;
            }
            finally
            {
                if (factory != IntPtr.Zero)
                {
                    ReleaseComObject(factory);
                }
            }
        }

        private static void ReleaseDxgiOutput(
            ref IntPtr dxgiOutput,
            ref WaitForVBlankDel? waitForVBlank,
            ref ReleaseComDel? releaseOutput)
        {
            if (dxgiOutput != IntPtr.Zero)
            {
                try
                {
                    if (releaseOutput != null)
                    {
                        releaseOutput(dxgiOutput);
                    }
                    else
                    {
                        ReleaseComObject(dxgiOutput);
                    }
                }
                catch
                {
                }
                dxgiOutput = IntPtr.Zero;
            }

            waitForVBlank = null;
            releaseOutput = null;
        }

        private static void ReleaseComObject(IntPtr obj)
        {
            if (obj == IntPtr.Zero)
            {
                return;
            }

            try
            {
                IntPtr vt = Marshal.ReadIntPtr(obj);
                var release = Marshal.GetDelegateForFunctionPointer<ReleaseComDel>(
                    Marshal.ReadIntPtr(vt, 2 * IntPtr.Size));
                release(obj);
            }
            catch
            {
            }
        }

        private static int QueryMonitorRefreshRateHz(IntPtr hMonitor)
        {
            try
            {
                string? deviceName = null;
                if (hMonitor != IntPtr.Zero)
                {
                    var mi = new MONITORINFOEX
                    {
                        cbSize = Marshal.SizeOf<MONITORINFOEX>()
                    };
                    if (GetMonitorInfoW(hMonitor, ref mi) && !string.IsNullOrWhiteSpace(mi.szDevice))
                    {
                        deviceName = mi.szDevice;
                    }
                }

                var dm = new DEVMODE
                {
                    dmSize = (ushort)Marshal.SizeOf<DEVMODE>()
                };
                if (EnumDisplaySettingsW(deviceName, EnumCurrentSettings, ref dm) &&
                    dm.dmDisplayFrequency >= 30 &&
                    dm.dmDisplayFrequency <= 1000)
                {
                    return (int)dm.dmDisplayFrequency;
                }
            }
            catch
            {
            }

            return 60;
        }

        private static Func<object, int>? CompileIntFieldGetter(Type targetType, string fieldName, BindingFlags flags)
        {
            FieldInfo? fi = targetType.GetField(fieldName, flags);
            if (fi == null || fi.FieldType != typeof(int))
            {
                return null;
            }

            ParameterExpression objArg = Expression.Parameter(typeof(object), "inst");
            MemberExpression fieldAccess = Expression.Field(Expression.Convert(objArg, targetType), fi);
            return Expression.Lambda<Func<object, int>>(fieldAccess, objArg).Compile();
        }

        private static Action<object, int>? CompileIntFieldSetter(Type targetType, string fieldName, BindingFlags flags)
        {
            FieldInfo? fi = targetType.GetField(fieldName, flags);
            if (fi == null || fi.FieldType != typeof(int))
            {
                return null;
            }

            ParameterExpression objArg = Expression.Parameter(typeof(object), "inst");
            ParameterExpression valArg = Expression.Parameter(typeof(int), "val");
            BinaryExpression assign = Expression.Assign(Expression.Field(Expression.Convert(objArg, targetType), fi), valArg);
            return Expression.Lambda<Action<object, int>>(assign, objArg, valArg).Compile();
        }

        private static Func<object, bool>? CompileBoolFieldGetter(Type targetType, string fieldName, BindingFlags flags)
        {
            FieldInfo? fi = targetType.GetField(fieldName, flags);
            if (fi == null || fi.FieldType != typeof(bool))
            {
                return null;
            }

            ParameterExpression objArg = Expression.Parameter(typeof(object), "inst");
            MemberExpression fieldAccess = Expression.Field(Expression.Convert(objArg, targetType), fi);
            return Expression.Lambda<Func<object, bool>>(fieldAccess, objArg).Compile();
        }

        private static Func<object, object?>? CompileObjectFieldGetter(Type targetType, string fieldName, BindingFlags flags)
        {
            FieldInfo? fi = targetType.GetField(fieldName, flags);
            if (fi == null)
            {
                return null;
            }

            ParameterExpression objArg = Expression.Parameter(typeof(object), "inst");
            UnaryExpression boxed = Expression.Convert(Expression.Field(Expression.Convert(objArg, targetType), fi), typeof(object));
            return Expression.Lambda<Func<object, object?>>(boxed, objArg).Compile();
        }

        private static Func<object, bool>? CompileParameterlessBoolMethod(Type targetType, string methodName, BindingFlags flags)
        {
            MethodInfo? mi = targetType.GetMethod(methodName, flags, null, Type.EmptyTypes, null);
            if (mi == null || mi.ReturnType != typeof(bool))
            {
                return null;
            }

            ParameterExpression objArg = Expression.Parameter(typeof(object), "inst");
            MethodCallExpression call = Expression.Call(Expression.Convert(objArg, targetType), mi);
            return Expression.Lambda<Func<object, bool>>(call, objArg).Compile();
        }

        private static Func<object, long, bool, double, bool, bool>? CompileTryUpdateMotionMethod(Type targetType, BindingFlags flags)
        {
            MethodInfo? mi = targetType.GetMethod(
                "TryUpdateCompositionMotionFrame",
                flags,
                null,
                new[] { typeof(long), typeof(bool), typeof(double), typeof(bool) },
                null);
            if (mi == null || mi.ReturnType != typeof(bool))
            {
                return null;
            }

            ParameterExpression inst = Expression.Parameter(typeof(object), "inst");
            ParameterExpression ts = Expression.Parameter(typeof(long), "ts");
            ParameterExpression drag = Expression.Parameter(typeof(bool), "drag");
            ParameterExpression minMs = Expression.Parameter(typeof(double), "minMs");
            ParameterExpression inputDriven = Expression.Parameter(typeof(bool), "inputDriven");

            MethodCallExpression call = Expression.Call(
                Expression.Convert(inst, targetType),
                mi,
                ts,
                drag,
                minMs,
                inputDriven);
            return Expression.Lambda<Func<object, long, bool, double, bool, bool>>(call, inst, ts, drag, minMs, inputDriven).Compile();
        }

        private void OnDockClosed(object? sender, EventArgs e)
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            AdaptiveSettingsStore.DockHighRefreshRateChanged -= OnSettingChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplayTopologyChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;

            _attachPollTimer.Stop();
            _attachPollTimer.Tick -= OnAttachPollTimerTick;
            _dockWindow.Loaded -= OnDockLifecycleWake;
            _dockWindow.IsVisibleChanged -= OnDockVisibleChanged;
            _dockWindow.LocationChanged -= OnDockGeometryWake;
            _dockWindow.SizeChanged -= OnDockSizeWake;
            _dockWindow.LayoutUpdated -= OnDockLayoutUpdated;
            _dockWindow.Closed -= OnDockClosed;
            UnhookCompositionWindow();

            try
            {
                _wakeEvent.Set();
            }
            catch
            {
            }

            try
            {
                _vsyncThread?.Join(300);
            }
            catch
            {
            }

            try
            {
                _wakeEvent.Dispose();
            }
            catch
            {
            }

            _vsyncThread = null;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdapters1Del(IntPtr factory, uint index, out IntPtr adapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumOutputsDel(IntPtr adapter, uint index, out IntPtr output);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetOutputDescDel(IntPtr output, out DXGI_OUTPUT_DESC desc);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int WaitForVBlankDel(IntPtr output);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseComDel(IntPtr obj);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            public RECT DesktopCoordinates;
            public int AttachedToDesktop;
            public int Rotation;
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
        }

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWaitableTimerExW(
            IntPtr lpTimerAttributes,
            string? lpTimerName,
            uint dwFlags,
            uint dwDesiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetWaitableTimerEx(
            IntPtr hTimer,
            ref long lpDueTime,
            int lPeriod,
            IntPtr pfnCompletionRoutine,
            IntPtr lpArgToCompletionRoutine,
            IntPtr wakeContext,
            uint tolerableDelay);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();
    }
}

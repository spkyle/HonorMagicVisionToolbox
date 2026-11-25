using System;
using System.Diagnostics;
using System.Linq.Expressions;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace TopBarAdaptiveColor
{
    /// <summary>
    /// Unlocks true native monitor refresh rate (120Hz / 144Hz / 165Hz / 240Hz) on Honor Magic Vision's
    /// bottom Dock (MagicTaskbarWpf.MainWindow) without modifying any official binaries:
    /// - Honor's Dock motion physics (NormalizeFrameEasing) and DirectComposition backend (DockCompositionHost)
    ///   already support arbitrary delta-time frames up to 240 FPS, but MainWindow throttles mouse hover updates
    ///   to 15.0 ms (~66 Hz), blocks OnRendering for 20.0 ms after input, and relies on WPF CompositionTarget.Rendering
    ///   which is engine-capped at 60 FPS on Windows.
    /// - While _isRenderingActive is true (0% CPU / zero wakeups when idle), this enhancer paces a lightweight
    ///   background thread to hardware DwmFlush() VSync and invokes MainWindow.TryUpdateCompositionMotionFrame via
    ///   compiled Expression delegates (0 reflection/allocations per frame) with minimumIntervalMs=3.5 and
    ///   inputDriven=true so WPF's 60Hz OnRendering and 15ms hover throttle yield automatically with zero double-stepping.
    /// </summary>
    internal sealed class DockHighRefreshRateEnhancer : IDisposable
    {
        private const double MinFrameIntervalMs = 3.5; // Supports up to 240Hz cleanly while avoiding sub-ms duplicate ticks
        private const int IdleGraceFlushes = 12;
        private const int MaxInitialAttachPolls = 30;

        private readonly Window _dockWindow;
        private readonly Action _tickAction;
        private readonly ManualResetEventSlim _wakeEvent = new(false);
        private readonly DispatcherTimer _attachPollTimer;

        // Compiled fast getters & invokers for MainWindow hot path (zero reflection/allocation per VSync).
        private readonly Func<object, bool>? _getIsRenderingActive;
        private readonly Func<object, bool>? _getDockWindowClosed;
        private readonly Func<object, bool>? _getCompEnabled;
        private readonly Func<object, object?>? _getCompHost;
        private readonly Func<object, bool>? _isDragActive;
        private readonly Func<object, long, bool, double, bool, bool>? _tryUpdateMotion;
        private readonly FieldInfo? _compositionNativeWindowField;

        private Type? _compHostCachedType;
        private Func<object, bool>? _getCompHostIsAttached;

        private object? _hookedCompositionWindow;
        private EventInfo? _hoverPosEvent;
        private Delegate? _hoverPosDelegate;
        private EventInfo? _hoveredItemEvent;
        private Delegate? _hoveredItemDelegate;

        private Thread? _vsyncThread;
        private int _initialAttachPolls;
        private int _frameDispatchPending;
        private volatile bool _disposed;

        public DockHighRefreshRateEnhancer(Window dockWindow)
        {
            _dockWindow = dockWindow ?? throw new ArgumentNullException(nameof(dockWindow));
            _tickAction = OnVsyncTickOnUi;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type mwType = dockWindow.GetType();

            _compositionNativeWindowField = mwType.GetField("_compositionNativeWindow", flags);
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

            if (!TryEnsureCompositionHooks())
            {
                _attachPollTimer.Start();
            }

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
                $"compHooked={_hookedCompositionWindow != null}).");
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
                SignalWake();
            }
        }

        private void OnDockVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!_disposed && e.NewValue is true)
            {
                TryEnsureCompositionHooks();
                SignalWake();
            }
        }

        private void OnDockGeometryWake(object? sender, EventArgs e)
        {
            if (!_disposed && AdaptiveSettingsStore.IsDockHighRefreshRateEnabled)
            {
                SignalWake();
            }
        }

        private void OnDockSizeWake(object sender, SizeChangedEventArgs e)
        {
            if (!_disposed && AdaptiveSettingsStore.IsDockHighRefreshRateEnabled)
            {
                SignalWake();
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
                bool hookedAny = false;

                EventInfo? posEvt = ncdwType.GetEvent("HoverPositionChanged", flags);
                MethodInfo? posAdd = posEvt?.GetAddMethod(nonPublic: true);
                if (posEvt?.EventHandlerType != null && posAdd != null)
                {
                    Delegate? posDel = BuildWakeDelegate(posEvt.EventHandlerType);
                    if (posDel != null)
                    {
                        posAdd.Invoke(ncdw, new object[] { posDel });
                        _hoverPosEvent = posEvt;
                        _hoverPosDelegate = posDel;
                        hookedAny = true;
                    }
                }

                EventInfo? itemEvt = ncdwType.GetEvent("HoveredItemChanged", flags);
                MethodInfo? itemAdd = itemEvt?.GetAddMethod(nonPublic: true);
                if (itemEvt?.EventHandlerType != null && itemAdd != null)
                {
                    Delegate? itemDel = BuildWakeDelegate(itemEvt.EventHandlerType);
                    if (itemDel != null)
                    {
                        itemAdd.Invoke(ncdw, new object[] { itemDel });
                        _hoveredItemEvent = itemEvt;
                        _hoveredItemDelegate = itemDel;
                        hookedAny = true;
                    }
                }

                if (hookedAny)
                {
                    _hookedCompositionWindow = ncdw;
                    DiagnosticLog.Info("DockHighRefreshRateEnhancer hooked NativeCompositionDockWindow hover wake events.");
                }

                return hookedAny;
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
                try
                {
                    if (_hoverPosEvent != null && _hoverPosDelegate != null)
                    {
                        _hoverPosEvent.GetRemoveMethod(nonPublic: true)?.Invoke(ncdw, new object[] { _hoverPosDelegate });
                    }
                }
                catch
                {
                }

                try
                {
                    if (_hoveredItemEvent != null && _hoveredItemDelegate != null)
                    {
                        _hoveredItemEvent.GetRemoveMethod(nonPublic: true)?.Invoke(ncdw, new object[] { _hoveredItemDelegate });
                    }
                }
                catch
                {
                }
            }

            _hookedCompositionWindow = null;
            _hoverPosEvent = null;
            _hoverPosDelegate = null;
            _hoveredItemEvent = null;
            _hoveredItemDelegate = null;
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

                    while (!_disposed && AdaptiveSettingsStore.IsDockHighRefreshRateEnabled)
                    {
                        if (!ShouldPumpFastCheck())
                        {
                            consecutiveIdleFlushes++;
                            if (consecutiveIdleFlushes >= IdleGraceFlushes)
                            {
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
                            consecutiveIdleFlushes = 0;
                        }

                        // Wait with 0% CPU for the next exact hardware DWM VSync (120Hz/144Hz/165Hz/240Hz).
                        int hr = DwmFlush();
                        if (hr != 0)
                        {
                            Thread.Sleep(16);
                            continue;
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
                _ = _tryUpdateMotion!(_dockWindow, now, dragActive, MinFrameIntervalMs, true);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"DockHighRefreshRateEnhancer UI tick failed: {ex.Message}");
            }
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
                _vsyncThread?.Join(250);
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

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();
    }
}

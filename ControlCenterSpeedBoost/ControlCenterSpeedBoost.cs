using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

[assembly: AssemblyTitle("ControlCenterSpeedBoost")]
[assembly: AssemblyProduct("ControlCenterSpeedBoost")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace ControlCenterSpeedBoost
{
    /// <summary>
    /// Loaded into ControlCenterTrayUI.exe (.NET Framework 4.8) via ControlCenterTrayUI.exe.config
    /// AppDomainManager. Watches %APPDATA%\taskbarAndAIbar\adaptive-topbar.json ("fast_settings_response")
    /// and eliminates AiDesktopMainWindow show/hide stalls & early-backdrop-hide flicker.
    /// </summary>
    public sealed class ControlCenterDomainManager : AppDomainManager
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
                ControlCenterWindowOptimizer.ApplyConfiguredRenderMode();

                EventManager.RegisterClassHandler(
                    typeof(Window),
                    FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnAnyWindowLoaded),
                    true);

                // AiDesktopMainWindow is created hidden at startup before its first visual LoadedEvent
                // bubbles on click #1. Poll briefly at startup so click #1 is already accelerated.
                _earlyAttachTimer = new Timer(TryEarlyAttachCallback, null, 150, 100);
                BoostLog.Info("ControlCenterDomainManager initialized.");
            }
            catch (Exception ex)
            {
                BoostLog.Warn("InitializeNewDomain failed: " + ex);
            }
        }

        private static void TryEarlyAttachCallback(object state)
        {
            try
            {
                if (Interlocked.Increment(ref _earlyAttachAttempts) > 100)
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

                        for (int i = 0; i < cur.Windows.Count; i++)
                        {
                            Window w = cur.Windows[i];
                            if (w != null && string.Equals(w.GetType().FullName, "ControlCenterTrayUI.AiDesktopMainWindow", StringComparison.Ordinal))
                            {
                                bool firstAttach = ControlCenterWindowOptimizer.Attach(w);
                                if (firstAttach)
                                {
                                    BoostLog.Info("Early-attached to AiDesktopMainWindow before first tray click.");
                                }
                                StopEarlyTimer();
                                return;
                            }
                        }
                    }
                    catch
                    {
                        // Retry until main window is constructed.
                    }
                }));
            }
            catch
            {
                // Ignore transient startup checks.
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

        private static void OnAnyWindowLoaded(object sender, RoutedEventArgs e)
        {
            Window w = sender as Window;
            if (w != null &&
                string.Equals(w.GetType().FullName, "ControlCenterTrayUI.AiDesktopMainWindow", StringComparison.Ordinal))
            {
                StopEarlyTimer();
                ControlCenterWindowOptimizer.Attach(w);
            }
        }
    }

    internal sealed class ControlCenterWindowOptimizer
    {
        private const string ShowStoryboardKey = "WndShowActionAiDesktop";
        private const string HideStoryboardKey = "WndHideActionAiDesktop";
        // Complete show/hide in <0.5ms so neither backdrop nor empty shadow flashes ahead of cards.
        private const double FastShowStoryboardSpeedRatio = 1000.0;
        private const double FastHideStoryboardSpeedRatio = 1000.0;
        private const uint BeforeShowCooldownMs = 1500;
        private const uint TrustCirclePollCooldownMs = 60000;
        // Long enough for TrustCircleView's async DealGetOnlineDevices IPC (~220ms) to resize
        // the bottom card while still DWM-cloaked & off-screen before the first user click.
        private const int PreWarmDurationMs = 340;
        private const int DefaultIdleTrimDelayMs = 180000;
        private const int DwmwaCloak = 13;

        private sealed class KeyframeBackup
        {
            public readonly DoubleKeyFrame Frame;
            public readonly double OriginalValue;

            public KeyframeBackup(DoubleKeyFrame frame, double originalValue)
            {
                Frame = frame;
                OriginalValue = originalValue;
            }
        }

        private static readonly ConditionalWeakTable<Window, ControlCenterWindowOptimizer> Attached =
            new ConditionalWeakTable<Window, ControlCenterWindowOptimizer>();

        private readonly Window _window;
        private readonly UIElement _gridWnd1;
        private readonly FieldInfo _lastHideTimeField;
        private readonly FieldInfo _isWndVisibleField;
        private readonly FieldInfo _providerField;
        private readonly FieldInfo _compHolderField;
        private readonly MethodInfo _limitOrUnlimitMemoryMethod;
        private readonly MethodInfo _setWindowToPrimaryScreenMethod;
        private readonly MethodInfo _toggleWndVisibleStateMethod;
        private readonly MethodInfo _showProviderMethod;
        private readonly MethodInfo _hideProviderMethod;
        private readonly Storyboard _showStoryboard;
        private readonly Storyboard _hideStoryboard;
        private readonly EventHandler _origHideCompletedDelegate;
        private readonly double _origShowSpeedRatio = 1.0;
        private readonly double _origHideSpeedRatio = 1.0;
        private readonly KeyframeBackup[] _showOpacityKeyframes;
        private readonly DispatcherTimer _preWarmFinishTimer;
        private readonly DispatcherTimer _idleTrimTimer;

        private bool _origHideHookDetached;

        private object _providerObj;
        private Window _compHolderWindow;
        private bool _compHolderEventsHooked;
        private object _cemInstance;
        private FieldInfo _onBeforeField;
        private Delegate _wrappedSubscribers;
        private Delegate _interceptorDelegate;
        private bool _isRunningBeforeShow;
        private bool _deferredRefreshQueued;
        private bool _hasInitialBeforeShow;
        private bool _hasLastBeforeShowTick;
        private int _lastBeforeShowTick;
        private bool _hasInitialTrustCircleShow;
        private int _lastTrustCircleTick;
        private bool _compHolderUncloakQueued;
        private bool _isPreWarmed;
        private bool _isPreWarming;
        private bool _insidePreWarmShow;
        private bool _preWarmCloakFailed;
        private bool _preWarmPrevShowActivated = true;
        private double _preWarmPrevOpacity = 1.0;
        private bool _windowFrameRendered;
        private FileSystemWatcher _configWatcher;

        private ControlCenterWindowOptimizer(Window window)
        {
            _window = window;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type winType = window.GetType();
            _lastHideTimeField = winType.GetField("lastHideTime", flags);
            _isWndVisibleField = winType.GetField("isWndVisible", flags);
            _providerField = winType.GetField("_provider", flags);
            _setWindowToPrimaryScreenMethod = winType.GetMethod("SetWindowToPrimaryScreen", flags);
            _toggleWndVisibleStateMethod = winType.GetMethod("ToggleWndVisibleState", flags);

            FieldInfo gridField = winType.GetField("gridWnd1", flags);
            _gridWnd1 = gridField != null ? gridField.GetValue(_window) as UIElement : null;
            if (_gridWnd1 == null)
            {
                _gridWnd1 = _window.FindName("gridWnd1") as UIElement;
            }

            if (_providerField != null)
            {
                _providerObj = _providerField.GetValue(_window);
                if (_providerObj != null)
                {
                    Type provType = _providerObj.GetType();
                    _compHolderField = provType.GetField("_compositionHandleHolder", flags);
                    _showProviderMethod = provType.GetMethod("ShowWindow", flags);
                    _hideProviderMethod = provType.GetMethod("HideWindow", flags);
                }
            }

            Type commToolsType = winType.Assembly.GetType("ControlCenterTrayUI.Common.MainUICommTools");
            if (commToolsType != null)
            {
                _limitOrUnlimitMemoryMethod = commToolsType.GetMethod("LimitOrUnlimitMemory", flags);
            }

            _showStoryboard = window.Resources[ShowStoryboardKey] as Storyboard;
            if (_showStoryboard != null)
            {
                _origShowSpeedRatio = _showStoryboard.SpeedRatio;
                _showOpacityKeyframes = SnapshotShowOpacityKeyframes(_showStoryboard);
            }
            else
            {
                _showOpacityKeyframes = new KeyframeBackup[0];
            }

            _hideStoryboard = window.Resources[HideStoryboardKey] as Storyboard;
            if (_hideStoryboard != null)
            {
                _origHideSpeedRatio = _hideStoryboard.SpeedRatio;
                MethodInfo origHideDone = winType.GetMethod("WndHideAction_Completed", flags);
                if (origHideDone != null)
                {
                    _origHideCompletedDelegate = (EventHandler)Delegate.CreateDelegate(
                        typeof(EventHandler), _window, origHideDone, false);
                }
                _hideStoryboard.Completed += OnHideStoryboardCompleted;
            }

            _preWarmFinishTimer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(PreWarmDurationMs)
            };
            _preWarmFinishTimer.Tick += OnPreWarmFinishTick;

            _idleTrimTimer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(BoostConfig.GetIdleTrimDelayMs(DefaultIdleTrimDelayMs))
            };
            _idleTrimTimer.Tick += OnIdleTrimTick;

            _window.SourceInitialized += OnWindowSourceInitialized;
            _window.IsVisibleChanged += OnWindowIsVisibleChanged;
            _window.Closed += OnWindowClosed;

            AttachCompHolderIfAvailable();
            StartConfigWatcher();
            ApplyLiveSettings();
            SyncBeforeShowInterceptor();

            // Catch plugins that register onto CommonEventManager right after MainWindow construction/load,
            // run OnBeforeAiDesktopUIShow once off-screen while DWM-cloaked so ControlPanel collapses and
            // TrustCircle card heights stabilize before the user's very first click.
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                AttachCompHolderIfAvailable();
                SyncBeforeShowInterceptor();
                ApplyLiveSettings();
                if (BoostConfig.IsFastResponseEnabled())
                {
                    UnlimitProcessMemory();
                    TryStartPreWarm();
                }
            }));

            BoostLog.Info(string.Format(
                "Attached to AiDesktopMainWindow (showSb={0}, showFrozen={1}, hideSb={2}, hideFrozen={3}, keyframes={4}, gridWnd1={5}, provider={6}, fast={7}).",
                _showStoryboard != null,
                _showStoryboard != null && _showStoryboard.IsFrozen,
                _hideStoryboard != null,
                _hideStoryboard != null && _hideStoryboard.IsFrozen,
                _showOpacityKeyframes.Length,
                _gridWnd1 != null,
                _providerObj != null,
                BoostConfig.IsFastResponseEnabled()));
        }

        private static KeyframeBackup[] SnapshotShowOpacityKeyframes(Storyboard sb)
        {
            var list = new System.Collections.Generic.List<KeyframeBackup>();
            try
            {
                if (sb == null)
                {
                    return list.ToArray();
                }

                for (int i = 0; i < sb.Children.Count; i++)
                {
                    DoubleAnimationUsingKeyFrames da = sb.Children[i] as DoubleAnimationUsingKeyFrames;
                    if (da == null || da.IsFrozen)
                    {
                        continue;
                    }

                    PropertyPath prop = Storyboard.GetTargetProperty(da);
                    if (prop == null || !string.Equals(prop.Path, "Opacity", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    for (int k = 0; k < da.KeyFrames.Count; k++)
                    {
                        DoubleKeyFrame kf = da.KeyFrames[k];
                        if (kf != null && !kf.IsFrozen)
                        {
                            list.Add(new KeyframeBackup(kf, kf.Value));
                        }
                    }
                }
            }
            catch
            {
                // Non-fatal.
            }
            return list.ToArray();
        }

        public static bool Attach(Window window)
        {
            if (window == null)
            {
                return false;
            }

            ControlCenterWindowOptimizer existing;
            if (Attached.TryGetValue(window, out existing))
            {
                existing.SyncBeforeShowInterceptor();
                existing.ApplyLiveSettings();
                return false;
            }

            Attached.GetValue(window, w => new ControlCenterWindowOptimizer(w));
            return true;
        }

        private void AttachCompHolderIfAvailable()
        {
            if (_providerObj == null || _compHolderField == null)
            {
                return;
            }

            try
            {
                if (_compHolderWindow == null)
                {
                    Window holder = _compHolderField.GetValue(_providerObj) as Window;
                    if (holder != null)
                    {
                        _compHolderWindow = holder;
                    }
                }

                if (_compHolderWindow != null && !_compHolderEventsHooked)
                {
                    _compHolderEventsHooked = true;
                    _compHolderWindow.SourceInitialized += OnCompHolderSourceInitialized;
                    _compHolderWindow.IsVisibleChanged += OnCompHolderIsVisibleChanged;
                }
            }
            catch
            {
                // Non-fatal.
            }
        }

        private void OnWindowSourceInitialized(object sender, EventArgs e)
        {
            if (_isPreWarming)
            {
                if (!TrySetWindowCloaked(_window, true))
                {
                    _preWarmCloakFailed = true;
                    ApplyPreWarmOffscreenFallback();
                }
            }
        }

        private void OnCompHolderSourceInitialized(object sender, EventArgs e)
        {
            if (_isPreWarming)
            {
                if (!TrySetWindowCloaked(_compHolderWindow, true))
                {
                    _preWarmCloakFailed = true;
                    ApplyPreWarmOffscreenFallback();
                }
            }
        }

        private void ApplyPreWarmOffscreenFallback()
        {
            try
            {
                _window.Left = -32000;
                _window.Top = -32000;
                _window.Opacity = 0.0;
                if (_compHolderWindow != null)
                {
                    _compHolderWindow.Opacity = 0.0;
                }
            }
            catch
            {
                // Ignore.
            }
        }

        private static bool TrySetWindowCloaked(Window win, bool cloak)
        {
            if (win == null)
            {
                return false;
            }

            try
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                if (hwnd == IntPtr.Zero)
                {
                    return false;
                }

                int val = cloak ? 1 : 0;
                return DwmSetWindowAttribute(hwnd, DwmwaCloak, ref val, sizeof(int)) == 0;
            }
            catch
            {
                return false;
            }
        }

        private bool IsWndVisible()
        {
            try
            {
                if (_isWndVisibleField != null)
                {
                    object val = _isWndVisibleField.GetValue(_window);
                    if (val is bool && (bool)val)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Ignore.
            }
            return false;
        }

        private void TryStartPreWarm()
        {
            if (_isPreWarmed || _isPreWarming || _window.IsVisible || IsWndVisible())
            {
                return;
            }

            _isPreWarmed = true;
            _isPreWarming = true;
            _preWarmCloakFailed = false;
            try
            {
                AttachCompHolderIfAvailable();
                SyncBeforeShowInterceptor();

                _preWarmPrevShowActivated = _window.ShowActivated;
                _preWarmPrevOpacity = _window.Opacity > 0.01 ? _window.Opacity : 1.0;

                // Position on primary screen so CompositionAcrylicProvider._dpi reads exact primary monitor DPI,
                // while DWMWA_CLOAK=1 in SourceInitialized keeps both windows 100% invisible at full Opacity=1.0
                // so WPF actually rasterizes all cards, SVGs, bitmaps, and TrustCircle layout.
                if (_setWindowToPrimaryScreenMethod != null)
                {
                    _setWindowToPrimaryScreenMethod.Invoke(_window, new object[] { 0 });
                }

                _window.ShowActivated = false;
                _window.Opacity = 1.0;
                if (_gridWnd1 != null)
                {
                    _gridWnd1.Opacity = 1.0;
                }

                if (_compHolderWindow != null)
                {
                    _compHolderWindow.ShowActivated = false;
                    _compHolderWindow.Opacity = 1.0;
                    if (new System.Windows.Interop.WindowInteropHelper(_compHolderWindow).Handle != IntPtr.Zero)
                    {
                        TrySetWindowCloaked(_compHolderWindow, true);
                    }
                }

                if (new System.Windows.Interop.WindowInteropHelper(_window).Handle != IntPtr.Zero)
                {
                    if (!TrySetWindowCloaked(_window, true))
                    {
                        _preWarmCloakFailed = true;
                        ApplyPreWarmOffscreenFallback();
                    }
                }

                // Run OnBeforeAiDesktopUIShow ONCE at startup before Show() so ControlCenterView switches out of
                // its initial ControlPanel=Visible state and TrustCircleView begins fetching its recommend card.
                if (_wrappedSubscribers != null)
                {
                    _isRunningBeforeShow = true;
                    try
                    {
                        InvokeWrappedSubscribers();
                        _hasInitialBeforeShow = true;
                        _lastBeforeShowTick = Environment.TickCount;
                        _hasLastBeforeShowTick = true;
                    }
                    finally
                    {
                        _isRunningBeforeShow = false;
                    }
                }

                _insidePreWarmShow = true;
                try
                {
                    _window.Show();
                    if (_showProviderMethod != null && _providerObj != null)
                    {
                        _showProviderMethod.Invoke(_providerObj, new object[] { _window });
                    }
                    _window.UpdateLayout();
                }
                finally
                {
                    _insidePreWarmShow = false;
                }

                _preWarmFinishTimer.Stop();
                _preWarmFinishTimer.Start();
            }
            catch (Exception ex)
            {
                BoostLog.Warn("TryStartPreWarm failed: " + ex.Message);
                FinishPreWarm();
            }
        }

        private void OnPreWarmFinishTick(object sender, EventArgs e)
        {
            FinishPreWarm();
        }

        internal void FinishPreWarm()
        {
            _preWarmFinishTimer.Stop();
            if (!_isPreWarming)
            {
                return;
            }

            try
            {
                if (!IsWndVisible())
                {
                    // Flush any async TrustCircle layout changes (e.g. ShowNetShareRecommend expanding Height 132->220)
                    // while still cloaked before hiding.
                    _window.UpdateLayout();

                    if (_hideProviderMethod != null && _providerObj != null)
                    {
                        _hideProviderMethod.Invoke(_providerObj, null);
                    }
                    _insidePreWarmShow = true;
                    try
                    {
                        _window.Visibility = Visibility.Hidden;
                    }
                    finally
                    {
                        _insidePreWarmShow = false;
                    }
                    if (_lastHideTimeField != null)
                    {
                        _lastHideTimeField.SetValue(_window, DateTime.MinValue);
                    }
                    if (_setWindowToPrimaryScreenMethod != null)
                    {
                        _setWindowToPrimaryScreenMethod.Invoke(_window, new object[] { 0 });
                    }
                }
            }
            catch (Exception ex)
            {
                BoostLog.Warn("FinishPreWarm cleanup failed: " + ex.Message);
            }
            finally
            {
                try
                {
                    TrySetWindowCloaked(_window, false);
                    TrySetWindowCloaked(_compHolderWindow, false);

                    bool fast = BoostConfig.IsFastResponseEnabled();
                    if (_compHolderWindow != null)
                    {
                        _compHolderWindow.Opacity = 1.0;
                    }
                    _window.Opacity = fast ? 1.0 : (_preWarmPrevOpacity > 0.01 ? _preWarmPrevOpacity : 1.0);
                    if (_gridWnd1 != null && fast)
                    {
                        _gridWnd1.Opacity = 1.0;
                    }
                    _window.ShowActivated = _preWarmPrevShowActivated;
                }
                catch
                {
                    // Ignore restore errors.
                }

                if (_isPreWarming && _preWarmCloakFailed)
                {
                    BoostLog.Warn("DWMWA_CLOAK unsupported during pre-warm; used off-screen Opacity=0 fallback.");
                }
                _isPreWarming = false;
                _windowFrameRendered = false;
                BoostLog.Info("Pre-warmed AiDesktopMainWindow, plugins & CompositionAcrylic surface cleanly.");
                ScheduleIdleTrimIfEnabled();
            }
        }

        private void AbortPreWarmForUserShow()
        {
            _preWarmFinishTimer.Stop();
            if (!_isPreWarming)
            {
                return;
            }

            _isPreWarming = false;
            try
            {
                TrySetWindowCloaked(_window, false);
                TrySetWindowCloaked(_compHolderWindow, false);
                if (_compHolderWindow != null)
                {
                    _compHolderWindow.Opacity = 1.0;
                }
                _window.Opacity = 1.0;
                if (_gridWnd1 != null)
                {
                    _gridWnd1.Opacity = 1.0;
                }
                _window.ShowActivated = _preWarmPrevShowActivated;
            }
            catch
            {
                // Non-fatal.
            }
        }

        private void StopStoryboardSafely(Storyboard sb)
        {
            if (sb == null || sb.IsFrozen)
            {
                return;
            }

            try
            {
                sb.Stop(_window);
            }
            catch
            {
                // Ignore if not started on _window.
            }

            try
            {
                sb.Remove(_window);
            }
            catch
            {
                // Ignore if not controllable on _window.
            }

            try
            {
                sb.Stop();
            }
            catch
            {
                // Ignore if not started without a target scope.
            }
        }

        private void OnCompHolderIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_isPreWarming || !BoostConfig.IsFastResponseEnabled())
            {
                return;
            }

            if (e.NewValue is bool && (bool)e.NewValue)
            {
                EnsureShowReadyOpacity();
                if (!_window.IsVisible || !_windowFrameRendered || _deferredRefreshQueued)
                {
                    // Prevent the acrylic backdrop window from appearing a frame ahead of MainWindow buttons.
                    TrySetWindowCloaked(_compHolderWindow, true);
                    ScheduleCompHolderUncloakAfterRender();
                }
                return;
            }

            if (e.NewValue is bool && !(bool)e.NewValue)
            {
                _windowFrameRendered = false;
                _compHolderUncloakQueued = false;
                // Hide main window before resetting opacity so the next open never shows a transparent
                // frame over the blur backdrop while waiting for VisibilityChanged to restore Opacity.
                try
                {
                    StopStoryboardSafely(_showStoryboard);
                    _window.Visibility = Visibility.Hidden;
                    _window.Opacity = 1.0;
                    if (_gridWnd1 != null)
                    {
                        _gridWnd1.Opacity = 1.0;
                    }
                    if (_lastHideTimeField != null)
                    {
                        _lastHideTimeField.SetValue(_window, DateTime.MinValue);
                    }
                    TrySetWindowCloaked(_window, false);
                    TrySetWindowCloaked(_compHolderWindow, false);
                }
                catch
                {
                    // Ignore if storyboard wasn't running yet.
                }
            }
        }

        private void OnWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_insidePreWarmShow)
            {
                return;
            }

            if (_isPreWarming && e.NewValue is bool && (bool)e.NewValue)
            {
                AbortPreWarmForUserShow();
            }

            bool fast = BoostConfig.IsFastResponseEnabled();
            if (e.NewValue is bool && (bool)e.NewValue)
            {
                _idleTrimTimer.Stop();
                if (fast)
                {
                    UnlimitProcessMemory();
                    StopStoryboardSafely(_hideStoryboard);
                }
                EnsureShowReadyOpacity();

                if (fast && (!_windowFrameRendered || _deferredRefreshQueued))
                {
                    TrySetWindowCloaked(_window, true);
                    if (_compHolderWindow != null)
                    {
                        TrySetWindowCloaked(_compHolderWindow, true);
                    }
                    ScheduleCompHolderUncloakAfterRender();
                }
                else
                {
                    TrySetWindowCloaked(_window, false);
                    if (fast && _compHolderWindow != null)
                    {
                        TrySetWindowCloaked(_compHolderWindow, false);
                    }
                }
            }
            else if (e.NewValue is bool && !(bool)e.NewValue)
            {
                _windowFrameRendered = false;
                _compHolderUncloakQueued = false;
                TrySetWindowCloaked(_window, false);
                TrySetWindowCloaked(_compHolderWindow, false);
                if (!fast)
                {
                    StopStoryboardSafely(_showStoryboard);
                    if (_gridWnd1 != null && _gridWnd1.Opacity > 0.01)
                    {
                        _gridWnd1.Opacity = 0.0;
                    }
                    if (_window.Opacity > 0.01)
                    {
                        _window.Opacity = 0.0;
                    }
                }
            }

            AttachCompHolderIfAvailable();
            ApplyLiveSettings();
            SyncBeforeShowInterceptor();
            if (!(e.NewValue is bool && (bool)e.NewValue))
            {
                ScheduleIdleTrimIfEnabled();
            }
        }

        private void OnHideStoryboardCompleted(object sender, EventArgs e)
        {
            if (!BoostConfig.IsFastResponseEnabled())
            {
                return;
            }

            try
            {
                if (!_window.IsVisible)
                {
                    StopStoryboardSafely(_hideStoryboard);
                    if (_window.Opacity < 0.99)
                    {
                        _window.Opacity = 1.0;
                    }
                }
                if (_gridWnd1 != null && _gridWnd1.Opacity < 0.99)
                {
                    _gridWnd1.Opacity = 1.0;
                }
                if (_lastHideTimeField != null)
                {
                    _lastHideTimeField.SetValue(_window, DateTime.MinValue);
                }

                if (_origHideHookDetached)
                {
                    if (_toggleWndVisibleStateMethod != null)
                    {
                        _toggleWndVisibleStateMethod.Invoke(_window, new object[] { "hide completed" });
                    }
                    else if (_isWndVisibleField != null)
                    {
                        _isWndVisibleField.SetValue(_window, false);
                    }
                }
            }
            catch
            {
                // Ignore if field layout differs.
            }

            UnlimitProcessMemory();
            SyncBeforeShowInterceptor();
            ScheduleIdleTrimIfEnabled();
        }

        private void UnlimitProcessMemory()
        {
            try
            {
                if (_limitOrUnlimitMemoryMethod != null)
                {
                    int pid = Process.GetCurrentProcess().Id;
                    _limitOrUnlimitMemoryMethod.Invoke(null, new object[] { false, pid });
                }
            }
            catch (Exception ex)
            {
                BoostLog.Warn("LimitOrUnlimitMemory(false) failed: " + ex.Message);
            }
        }

        internal static void ApplyConfiguredRenderMode()
        {
            try
            {
                RenderMode desired = (BoostConfig.IsFastResponseEnabled() && BoostConfig.IsSoftwareRenderingEnabled())
                    ? RenderMode.SoftwareOnly
                    : RenderMode.Default;
                if (RenderOptions.ProcessRenderMode != desired)
                {
                    RenderOptions.ProcessRenderMode = desired;
                    BoostLog.Info("Applied ControlCenter ProcessRenderMode=" + desired);
                }
            }
            catch (Exception ex)
            {
                BoostLog.Warn("ApplyConfiguredRenderMode failed: " + ex.Message);
            }
        }

        private void ScheduleIdleTrimIfEnabled()
        {
            if (BoostConfig.IsFastResponseEnabled() &&
                BoostConfig.IsIdleMemoryTrimEnabled() &&
                !_isPreWarming &&
                !_window.IsVisible &&
                !IsWndVisible())
            {
                _idleTrimTimer.Interval = TimeSpan.FromMilliseconds(BoostConfig.GetIdleTrimDelayMs(DefaultIdleTrimDelayMs));
                _idleTrimTimer.Stop();
                _idleTrimTimer.Start();
            }
            else
            {
                _idleTrimTimer.Stop();
            }
        }

        private void SyncIdleTrimTimer()
        {
            if (BoostConfig.IsFastResponseEnabled() &&
                BoostConfig.IsIdleMemoryTrimEnabled() &&
                !_isPreWarming &&
                !_window.IsVisible &&
                !IsWndVisible())
            {
                TimeSpan desired = TimeSpan.FromMilliseconds(BoostConfig.GetIdleTrimDelayMs(DefaultIdleTrimDelayMs));
                if (_idleTrimTimer.Interval != desired)
                {
                    _idleTrimTimer.Interval = desired;
                }
                if (!_idleTrimTimer.IsEnabled)
                {
                    _idleTrimTimer.Start();
                }
            }
            else
            {
                _idleTrimTimer.Stop();
            }
        }

        private void OnIdleTrimTick(object sender, EventArgs e)
        {
            _idleTrimTimer.Stop();
            if (!BoostConfig.IsFastResponseEnabled() ||
                !BoostConfig.IsIdleMemoryTrimEnabled() ||
                _isPreWarming ||
                _window.IsVisible ||
                IsWndVisible())
            {
                return;
            }

            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false, true);
                using (Process proc = Process.GetCurrentProcess())
                {
                    SetProcessWorkingSetSize(proc.Handle, new IntPtr(-1), new IntPtr(-1));
                }
                BoostLog.Info("Soft-trimmed idle ControlCenterTrayUI working set (fast_open_idle_memory_trim=true).");
            }
            catch (Exception ex)
            {
                BoostLog.Warn("ControlCenter idle working-set trim failed: " + ex.Message);
            }
        }

        private void ApplyLiveSettings()
        {
            ApplyConfiguredRenderMode();
            bool fast = BoostConfig.IsFastResponseEnabled();

            try
            {
                if (_showStoryboard != null && !_showStoryboard.IsFrozen)
                {
                    double target = fast ? FastShowStoryboardSpeedRatio : _origShowSpeedRatio;
                    if (Math.Abs(_showStoryboard.SpeedRatio - target) > 0.01)
                    {
                        _showStoryboard.SpeedRatio = target;
                    }

                    for (int i = 0; i < _showOpacityKeyframes.Length; i++)
                    {
                        KeyframeBackup kb = _showOpacityKeyframes[i];
                        if (!kb.Frame.IsFrozen)
                        {
                            double desiredVal = fast ? 1.0 : kb.OriginalValue;
                            if (Math.Abs(kb.Frame.Value - desiredVal) > 0.001)
                            {
                                kb.Frame.Value = desiredVal;
                            }
                        }
                    }
                }

                if (_gridWnd1 != null)
                {
                    if (fast)
                    {
                        if (_gridWnd1.Opacity < 0.99)
                        {
                            _gridWnd1.Opacity = 1.0;
                        }
                    }
                    else if (!_window.IsVisible)
                    {
                        StopStoryboardSafely(_showStoryboard);
                        if (_gridWnd1.Opacity > 0.01)
                        {
                            _gridWnd1.Opacity = 0.0;
                        }
                    }
                }

                if (!fast)
                {
                    TrySetWindowCloaked(_window, false);
                    TrySetWindowCloaked(_compHolderWindow, false);
                }
            }
            catch (Exception ex)
            {
                BoostLog.Warn("ShowStoryboard update failed: " + ex.Message);
            }

            try
            {
                if (_hideStoryboard != null && !_hideStoryboard.IsFrozen)
                {
                    double target = fast ? FastHideStoryboardSpeedRatio : _origHideSpeedRatio;
                    if (Math.Abs(_hideStoryboard.SpeedRatio - target) > 0.01)
                    {
                        _hideStoryboard.SpeedRatio = target;
                    }

                    if (fast && !_origHideHookDetached && _origHideCompletedDelegate != null)
                    {
                        _hideStoryboard.Completed -= _origHideCompletedDelegate;
                        _origHideHookDetached = true;
                        BoostLog.Info("Bypassed AiDesktopMainWindow.WndHideAction_Completed (ReleaseMemory/WorkingSet trim disabled).");
                    }
                    else if (!fast && _origHideHookDetached && _origHideCompletedDelegate != null)
                    {
                        _hideStoryboard.Completed += _origHideCompletedDelegate;
                        _origHideHookDetached = false;
                    }
                }
            }
            catch (Exception ex)
            {
                BoostLog.Warn("HideStoryboard SpeedRatio update failed: " + ex.Message);
            }

            SyncIdleTrimTimer();
        }

        private static bool ContainsDelegate(Delegate combined, Delegate candidate)
        {
            if (combined == null || candidate == null)
            {
                return false;
            }

            Delegate[] existing = combined.GetInvocationList();
            for (int i = 0; i < existing.Length; i++)
            {
                if (existing[i] == candidate)
                {
                    return true;
                }
            }
            return false;
        }

        private void SyncBeforeShowInterceptor()
        {
            try
            {
                if (_cemInstance == null || _onBeforeField == null)
                {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                    Assembly baseAsm = FindLoadedAssembly("ControlCenterPluginBase");
                    Type cemType = baseAsm != null ? baseAsm.GetType("ControlCenterPluginBase.CommonEventManager") : null;
                    if (cemType == null)
                    {
                        return;
                    }

                    MethodInfo getInst = cemType.GetMethod("GetInstance", flags);
                    if (getInst == null)
                    {
                        return;
                    }

                    _cemInstance = getInst.Invoke(null, null);
                    _onBeforeField = cemType.GetField("OnBeforeAiDesktopUIShow", flags);
                    if (_cemInstance == null || _onBeforeField == null)
                    {
                        return;
                    }

                    MethodInfo hookMethod = typeof(ControlCenterWindowOptimizer).GetMethod(
                        "InterceptedBeforeAiDesktopUIShow",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    if (hookMethod == null)
                    {
                        return;
                    }

                    _interceptorDelegate = Delegate.CreateDelegate(_onBeforeField.FieldType, this, hookMethod);
                }

                if (_cemInstance == null || _onBeforeField == null || _interceptorDelegate == null)
                {
                    return;
                }

                Delegate current = _onBeforeField.GetValue(_cemInstance) as Delegate;
                if (current == null)
                {
                    if (_wrappedSubscribers != null)
                    {
                        _onBeforeField.SetValue(_cemInstance, _interceptorDelegate);
                    }
                    return;
                }

                Delegate[] list = current.GetInvocationList();
                if (list.Length == 1 && ReferenceEquals(list[0].Target, this))
                {
                    return;
                }

                int added = 0;
                for (int i = 0; i < list.Length; i++)
                {
                    Delegate d = list[i];
                    if (!ReferenceEquals(d.Target, this) && !ContainsDelegate(_wrappedSubscribers, d))
                    {
                        _wrappedSubscribers = _wrappedSubscribers == null
                            ? d
                            : Delegate.Combine(_wrappedSubscribers, d);
                        added++;
                    }
                }

                _onBeforeField.SetValue(_cemInstance, _interceptorDelegate);
                if (added > 0)
                {
                    BoostLog.Info(string.Format("Wrapped {0} OnBeforeAiDesktopUIShow subscriber(s).", added));
                }
            }
            catch (Exception ex)
            {
                BoostLog.Warn("SyncBeforeShowInterceptor failed: " + ex.Message);
            }
        }

        private void InterceptedBeforeAiDesktopUIShow()
        {
            if (_isPreWarming)
            {
                AbortPreWarmForUserShow();
            }

            if (!BoostConfig.IsFastResponseEnabled())
            {
                InvokeWrappedSubscribers();
                return;
            }

            if (_wrappedSubscribers == null || _isRunningBeforeShow)
            {
                return;
            }

            // Once pre-warmed/initialized, never stall ShowWindow() with 300-500ms synchronous WLAN/dGPU queries.
            // Open immediately on frame 1 using existing warm cards, then refresh subscribers on Loaded/Background.
            if (!_hasInitialBeforeShow)
            {
                _isRunningBeforeShow = true;
                try
                {
                    _lastBeforeShowTick = Environment.TickCount;
                    _hasLastBeforeShowTick = true;
                    _hasInitialBeforeShow = true;
                    InvokeWrappedSubscribers();
                    _window.UpdateLayout();
                }
                finally
                {
                    _isRunningBeforeShow = false;
                    EnsureShowReadyOpacity();
                }
                return;
            }

            EnsureShowReadyOpacity();
            uint elapsed = unchecked((uint)Environment.TickCount - (uint)_lastBeforeShowTick);
            if (_hasLastBeforeShowTick && elapsed < BeforeShowCooldownMs)
            {
                return;
            }

            if (_deferredRefreshQueued)
            {
                return;
            }

            _deferredRefreshQueued = true;
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                _deferredRefreshQueued = false;
                if (!BoostConfig.IsFastResponseEnabled() || _wrappedSubscribers == null || _isRunningBeforeShow)
                {
                    if (_window.IsVisible && !_isPreWarming)
                    {
                        ScheduleCompHolderUncloakAfterRender();
                    }
                    return;
                }

                _isRunningBeforeShow = true;
                try
                {
                    _lastBeforeShowTick = Environment.TickCount;
                    _hasLastBeforeShowTick = true;
                    InvokeWrappedSubscribers();
                }
                finally
                {
                    _isRunningBeforeShow = false;
                    if (_window.IsVisible && !_isPreWarming)
                    {
                        ScheduleCompHolderUncloakAfterRender();
                    }
                }
            }));
        }

        private void ScheduleCompHolderUncloakAfterRender()
        {
            if (_compHolderUncloakQueued || _deferredRefreshQueued || _window.Dispatcher == null || _window.Dispatcher.HasShutdownStarted)
            {
                return;
            }

            _compHolderUncloakQueued = true;
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(delegate
            {
                _compHolderUncloakQueued = false;
                if (!_isPreWarming && _window.IsVisible)
                {
                    _windowFrameRendered = true;
                    TrySetWindowCloaked(_window, false);
                    TrySetWindowCloaked(_compHolderWindow, false);
                }
            }));
        }

        private void EnsureShowReadyOpacity()
        {
            try
            {
                if (_compHolderWindow != null && _compHolderWindow.Opacity < 0.99)
                {
                    _compHolderWindow.Opacity = 1.0;
                }
                if (_window.Opacity < 0.99)
                {
                    _window.Opacity = 1.0;
                }
                if (_gridWnd1 != null && _gridWnd1.Opacity < 0.99)
                {
                    _gridWnd1.Opacity = 1.0;
                }
            }
            catch
            {
                // Ignore.
            }
        }

        private static bool IsTrustCircleSubscriber(Delegate d)
        {
            try
            {
                MethodInfo mi = d != null ? d.Method : null;
                Type dt = mi != null ? mi.DeclaringType : null;
                if (dt != null && dt.FullName != null &&
                    dt.FullName.IndexOf("TrustCircle", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
                return mi != null && mi.Name != null &&
                    mi.Name.IndexOf("TrustCircle", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private void InvokeWrappedSubscribers()
        {
            Delegate subs = _wrappedSubscribers;
            if (subs == null)
            {
                return;
            }

            bool throttleTrustCircle = !_isPreWarming && BoostConfig.IsFastResponseEnabled();
            Delegate[] list = subs.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                Delegate d = list[i];
                if (throttleTrustCircle && IsTrustCircleSubscriber(d) && _hasInitialTrustCircleShow)
                {
                    uint tcElapsed = unchecked((uint)Environment.TickCount - (uint)_lastTrustCircleTick);
                    if (tcElapsed < TrustCirclePollCooldownMs)
                    {
                        continue;
                    }
                }

                try
                {
                    d.DynamicInvoke();
                    if (IsTrustCircleSubscriber(d))
                    {
                        _hasInitialTrustCircleShow = true;
                        _lastTrustCircleTick = Environment.TickCount;
                    }
                }
                catch (Exception ex)
                {
                    BoostLog.Warn("Wrapped BeforeUIShow handler threw: " + ex);
                }
            }
        }

        private static Assembly FindLoadedAssembly(string simpleName)
        {
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                if (string.Equals(asms[i].GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                {
                    return asms[i];
                }
            }
            return null;
        }

        private void StartConfigWatcher()
        {
            try
            {
                string dir = Path.GetDirectoryName(BoostConfig.ConfigPath) ?? string.Empty;
                if (string.IsNullOrEmpty(dir))
                {
                    return;
                }

                Directory.CreateDirectory(dir);
                _configWatcher = new FileSystemWatcher(dir, Path.GetFileName(BoostConfig.ConfigPath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
                };
                _configWatcher.Changed += OnConfigFileChanged;
                _configWatcher.Created += OnConfigFileChanged;
                _configWatcher.Renamed += OnConfigFileChanged;
                _configWatcher.EnableRaisingEvents = true;
            }
            catch
            {
                // Non-fatal: IsVisibleChanged still reloads config by mtime.
            }
        }

        private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
        {
            if (_window.Dispatcher.HasShutdownStarted)
            {
                return;
            }

            _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate
            {
                BoostConfig.InvalidateCache();
                ApplyLiveSettings();
                if (BoostConfig.IsFastResponseEnabled())
                {
                    UnlimitProcessMemory();
                }
            }));
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            _preWarmFinishTimer.Stop();
            _preWarmFinishTimer.Tick -= OnPreWarmFinishTick;
            _idleTrimTimer.Stop();
            _idleTrimTimer.Tick -= OnIdleTrimTick;
            _window.SourceInitialized -= OnWindowSourceInitialized;
            _window.IsVisibleChanged -= OnWindowIsVisibleChanged;
            _window.Closed -= OnWindowClosed;
            TrySetWindowCloaked(_window, false);
            if (_compHolderWindow != null)
            {
                TrySetWindowCloaked(_compHolderWindow, false);
                if (_compHolderEventsHooked)
                {
                    _compHolderWindow.SourceInitialized -= OnCompHolderSourceInitialized;
                    _compHolderWindow.IsVisibleChanged -= OnCompHolderIsVisibleChanged;
                }
                _compHolderWindow = null;
            }
            if (_hideStoryboard != null)
            {
                _hideStoryboard.Completed -= OnHideStoryboardCompleted;
                if (_origHideHookDetached && !_hideStoryboard.IsFrozen && _origHideCompletedDelegate != null)
                {
                    _hideStoryboard.Completed += _origHideCompletedDelegate;
                    _origHideHookDetached = false;
                }
            }
            if (_configWatcher != null)
            {
                _configWatcher.EnableRaisingEvents = false;
                _configWatcher.Dispose();
                _configWatcher = null;
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);
    }

    internal static class BoostConfig
    {
        public static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "taskbarAndAIbar",
            "adaptive-topbar.json");

        private static readonly Regex FastSettingRegex = new Regex(
            "\"fast_settings_response\"\\s*:\\s*(true|false)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex SoftwareRenderingRegex = new Regex(
            "\"fast_open_software_rendering\"\\s*:\\s*(true|false)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex IdleTrimRegex = new Regex(
            "\"fast_open_idle_memory_trim\"\\s*:\\s*(true|false)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly object Sync = new object();
        private static DateTime _lastWriteUtc = DateTime.MinValue;
        private static bool _cachedEnabled = true;
        private static bool _cachedSoftwareRendering = true;
        private static bool _cachedIdleTrim = true;

        public static void InvalidateCache()
        {
            lock (Sync)
            {
                _lastWriteUtc = DateTime.MinValue;
            }
        }

        public static int GetIdleTrimDelayMs(int defaultMs)
        {
            string raw = Environment.GetEnvironmentVariable("MAGICTASKBAR_TEST_IDLE_TRIM_MS");
            int parsed;
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out parsed) && parsed >= 20)
            {
                return parsed;
            }
            return defaultMs;
        }

        public static bool IsFastResponseEnabled()
        {
            EnsureLoaded();
            lock (Sync)
            {
                return _cachedEnabled;
            }
        }

        public static bool IsSoftwareRenderingEnabled()
        {
            EnsureLoaded();
            lock (Sync)
            {
                return _cachedSoftwareRendering;
            }
        }

        public static bool IsIdleMemoryTrimEnabled()
        {
            EnsureLoaded();
            lock (Sync)
            {
                return _cachedIdleTrim;
            }
        }

        private static bool ParseFlag(string text, Regex regex, bool defaultValue)
        {
            Match m = regex.Match(text);
            if (!m.Success)
            {
                return defaultValue;
            }
            return string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureLoaded()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    lock (Sync)
                    {
                        _cachedEnabled = true;
                        _cachedSoftwareRendering = true;
                        _cachedIdleTrim = true;
                        _lastWriteUtc = DateTime.MinValue;
                    }
                    return;
                }

                DateTime mtime = File.GetLastWriteTimeUtc(ConfigPath);
                lock (Sync)
                {
                    if (mtime == _lastWriteUtc)
                    {
                        return;
                    }

                    string text = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    _cachedEnabled = ParseFlag(text, FastSettingRegex, true);
                    _cachedSoftwareRendering = ParseFlag(text, SoftwareRenderingRegex, true);
                    _cachedIdleTrim = ParseFlag(text, IdleTrimRegex, true);
                    _lastWriteUtc = mtime;
                }
            }
            catch
            {
                lock (Sync)
                {
                    _cachedEnabled = true;
                    _cachedSoftwareRendering = true;
                    _cachedIdleTrim = true;
                }
            }
        }
    }

    internal static class BoostLog
    {
        private static readonly object Sync = new object();
        private static readonly string LogPath = Path.Combine(
            Path.GetTempPath(),
            "MagicTaskbar",
            "control-center-boost.log");

        public static void Info(string message) { Write("INFO", message); }
        public static void Warn(string message) { Write("WARN", message); }

        private static void Write(string level, string message)
        {
            try
            {
                string dir = Path.GetDirectoryName(LogPath) ?? string.Empty;
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}", DateTime.Now, level, message, Environment.NewLine);
                lock (Sync)
                {
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024)
                    {
                        File.Delete(LogPath);
                    }
                    File.AppendAllText(LogPath, line, Encoding.UTF8);
                }
            }
            catch
            {
                // Never throw from logging.
            }
        }
    }
}

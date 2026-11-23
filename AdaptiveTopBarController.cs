using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TopBarAdaptiveColor
{
    internal sealed class AdaptiveTopBarController : IDisposable
    {
        private const uint EventSystemForeground = 0x0003;
        private const uint EventSystemMoveSizeEnd = 0x000B;
        private const uint EventSystemMinimizeStart = 0x0016;
        private const uint EventSystemMinimizeEnd = 0x0017;
        private const uint EventObjectDestroy = 0x8001;
        private const uint EventObjectShow = 0x8002;
        private const uint EventObjectHide = 0x8003;
        private const uint EventObjectLocationChange = 0x800B;
        private const uint WinEventOutOfContext = 0x0000;
        private const uint WinEventSkipOwnProcess = 0x0002;
        private const uint SwpNomove = 0x0002;
        private const uint SwpNozorder = 0x0004;
        private const uint SwpNoactivate = 0x0010;
        private const double DefaultTopBarHeightDip = 32.0;

        private const double DarkForegroundLuminanceEnter = 0.58;
        private const double LightForegroundLuminanceEnter = 0.48;
        private const double InitialLuminanceThreshold = 0.53;

        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
        private static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(10);
        private static readonly TimeSpan BurstInterval = TimeSpan.FromMilliseconds(55);
        private static readonly TimeSpan ColorTransitionDuration = TimeSpan.FromMilliseconds(50);

        private static readonly Color DarkInkColor = Color.FromRgb(0x1F, 0x1F, 0x1F);
        private static readonly Color LightInkColor = Color.FromRgb(0xFF, 0xFF, 0xFF);

        private static readonly string[] AdaptiveResourceKeys =
        {
            "TopBarForegroundBrush",
            "TopBarSecondaryForegroundBrush",
            "TopBarHoverBrush",
            "TopBarPressedBrush",
            "TopBarSelectedBrush",
            "TopBarAiRecommendSelectedBrush"
        };

        private readonly Window _window;
        private readonly Border? _backgroundBorder;
        private readonly Panel? _inputRoot;
        private readonly SvgIconThemer _svgThemer = new();
        private readonly TopBarUiEnhancer _uiEnhancer;
        private readonly TopBarMaximizedWindowController _windowDragController;

        private readonly FieldInfo? _toolbarModeField;
        private readonly FieldInfo? _fixedFullscreenHiddenField;
        private readonly FieldInfo? _displayTransitionHiddenField;
        private readonly FieldInfo? _closingTopologyField;
        private readonly FieldInfo? _barModePhaseField;
        private readonly FieldInfo? _acrylicEnabledField;
        private readonly FieldInfo? _smartBackdropActiveField;
        private readonly FieldInfo? _toolbarModePopupOpenField;
        private readonly FieldInfo? _desktopLayerHostField;
        private readonly FieldInfo? _nativeFixedVisualActiveField;
        private readonly FieldInfo? _appBarManagerField;
        private readonly PropertyInfo? _isAnyPopupOpenProp;
        private readonly MethodInfo? _applyBackgroundStateMethod;
        private readonly MethodInfo? _syncSmartBackdropMethod;
        private readonly MethodInfo? _hideSmartBackdropMethod;
        private readonly MethodInfo? _restoreWpfVisualMethod;
        private readonly MethodInfo? _getTopBarHeightPixelsMethod;
        private readonly MethodInfo? _applyFixedFullscreenMethod;
        private FrameworkElement? _rootContent;

        private sealed record ShapeBrushPair(Brush? Fill, Brush? Stroke);

        private readonly Dictionary<string, Color> _originalResourceColors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Brush?> _originalResourceBrushes = new(StringComparer.Ordinal);
        private readonly ConditionalWeakTable<TextBlock, Brush?> _originalTextForegrounds = new();
        private readonly ConditionalWeakTable<Border, Brush?> _originalMaskBackgrounds = new();
        private readonly ConditionalWeakTable<Shape, ShapeBrushPair> _originalShapeBrushes = new();

        private readonly SolidColorBrush _adaptiveBackgroundBrush;
        private readonly SolidColorBrush _darkTextBrush;
        private readonly SolidColorBrush _darkBatteryTextBrush;
        private readonly DispatcherTimer _pollTimer;
        private readonly DispatcherTimer _coalesceTimer;
        private readonly DispatcherTimer _burstTimer;
        private readonly List<ItemsControl> _dynamicItemsControls = new();
        private readonly Dictionary<IntPtr, long> _minimizingExclusions = new();
        private readonly List<IntPtr> _activeExclusionBuffer = new();

        private Brush? _originalDefaultBackgroundBrush;
        private EventHandler? _borderBackgroundChangedHandler;
        private System.ComponentModel.DependencyPropertyDescriptor? _borderBackgroundDescriptor;
        private WinEventDelegate? _winEventDelegate;
        private IntPtr _hookForeground;
        private IntPtr _hookShowHide;
        private IntPtr _hookLocation;

        private IntPtr _hwnd;
        private bool _disposed;
        private bool _isSettingBackground;
        private bool _isMatched;
        private bool _useDarkForeground;
        private Color? _currentTargetColor;
        private IntPtr _lastMatchedHwnd;
        private long _lastFullscreenRecoverTick;
        private long _lastDarkForegroundTreeSyncTick;
        private const long DarkForegroundTreeSyncIntervalMs = 1500;
        private int _burstRemaining;
        private double _baseWindowHeightDip = DefaultTopBarHeightDip;
        private double _origWindowHeight = double.NaN;
        private double _origWindowMaxHeight = double.PositiveInfinity;
        private double _origBorderHeight = double.NaN;
        private VerticalAlignment _origBorderVerticalAlignment = VerticalAlignment.Stretch;
        private Thickness _origBorderMargin;
        private double _origInputHeight = double.NaN;
        private VerticalAlignment _origInputVerticalAlignment = VerticalAlignment.Stretch;
        private double _origRootHeight = double.NaN;
        private double _origRootMaxHeight = double.PositiveInfinity;
        private VerticalAlignment _origRootVerticalAlignment = VerticalAlignment.Stretch;
        private bool _isSettingWindowSize;
        private bool _isHeightExtended;
        private int _appliedHeightPx;
        private int _appliedBaseHeightPx;
        private int _lastAppliedWidthPx;
        private double _lastDpiScale = 1.0;

        public AdaptiveTopBarController(Window topBarWindow)
        {
            _window = topBarWindow;

            Type t = topBarWindow.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

            _backgroundBorder = t.GetField("TopBarBackgroundBorder", flags)?.GetValue(topBarWindow) as Border;
            _inputRoot = t.GetField("TopBarInputRoot", flags)?.GetValue(topBarWindow) as Panel;

            _toolbarModeField = t.GetField("_toolbarVisibilityMode", flags);
            _fixedFullscreenHiddenField = t.GetField("_fixedFullscreenHidden", flags);
            _displayTransitionHiddenField = t.GetField("_displayTransitionHidden", flags);
            _closingTopologyField = t.GetField("_closingForDisplayTopologyChange", flags);
            _barModePhaseField = t.GetField("_barModePhase", flags);
            _acrylicEnabledField = t.GetField("_topBarAcrylicEnabled", flags);
            _smartBackdropActiveField = t.GetField("_smartCompositionBackdropActive", flags);
            _toolbarModePopupOpenField = t.GetField("_toolbarModePopupOpen", flags);
            _desktopLayerHostField = t.GetField("_desktopLayerHost", flags);
            _nativeFixedVisualActiveField = t.GetField("_nativeFixedVisualActive", flags);
            _appBarManagerField = t.GetField("_appBarManager", flags);

            _isAnyPopupOpenProp = t.GetProperty("IsAnyTopBarPopupOpen", flags);
            _applyBackgroundStateMethod = t.GetMethod("ApplyTopBarBackgroundVisualState", flags);
            _syncSmartBackdropMethod = t.GetMethod("SyncSmartCompositionBackdrop", flags);
            _restoreWpfVisualMethod = t.GetMethod("RestoreWpfTopBarVisual", flags);
            _getTopBarHeightPixelsMethod = t.GetMethod("GetTopBarHeightPixels", flags);
            _applyFixedFullscreenMethod = t.GetMethod("ApplyFixedFullscreenVisibility", flags);

            if (_desktopLayerHostField != null)
            {
                object? host = _desktopLayerHostField.GetValue(topBarWindow);
                _hideSmartBackdropMethod = host?.GetType().GetMethod("HideSmartBackdrop", flags);
            }

            _adaptiveBackgroundBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            _darkTextBrush = new SolidColorBrush(DarkInkColor);
            _darkTextBrush.Freeze();
            _darkBatteryTextBrush = new SolidColorBrush(Color.FromArgb(0xE6, DarkInkColor.R, DarkInkColor.G, DarkInkColor.B));
            _darkBatteryTextBrush.Freeze();

            SnapshotOriginalResources();
            CaptureBaseLayoutSnapshot();
            HookBackgroundGuard();
            HookDynamicItemsControls();
            InstallWinEventHooks();

            _coalesceTimer = new DispatcherTimer(DispatcherPriority.Send, _window.Dispatcher)
            {
                Interval = CoalesceDelay
            };
            _coalesceTimer.Tick += (_, _) =>
            {
                _coalesceTimer.Stop();
                EvaluateAndApply();
            };

            _burstTimer = new DispatcherTimer(DispatcherPriority.Normal, _window.Dispatcher)
            {
                Interval = BurstInterval
            };
            _burstTimer.Tick += (_, _) =>
            {
                if (_burstRemaining <= 0)
                {
                    _burstTimer.Stop();
                    return;
                }
                _burstRemaining--;
                EvaluateAndApply();
                if (_burstRemaining <= 0)
                {
                    _burstTimer.Stop();
                }
            };

            _pollTimer = new DispatcherTimer(DispatcherPriority.Normal, _window.Dispatcher)
            {
                Interval = PollInterval
            };
            _pollTimer.Tick += (_, _) => EvaluateAndApply();
            _pollTimer.Start();

            _window.Closed += OnWindowClosed;
            _window.SizeChanged += OnWindowSizeChanged;
            _window.IsVisibleChanged += (_, _) => ScheduleCoalescedEvaluate();
            AdaptiveSettingsStore.EnabledChanged += OnAdaptiveSettingChanged;
            AdaptiveSettingsStore.GapFixChanged += OnGapFixSettingChanged;
            _uiEnhancer = new TopBarUiEnhancer(topBarWindow);
            _windowDragController = new TopBarMaximizedWindowController(topBarWindow);

            DiagnosticLog.Info($"AdaptiveTopBarController attached to TopBarWindow (svgThemer={_svgThemer.IsAvailable}, enabled={AdaptiveSettingsStore.IsEnabled}).");
            EvaluateAndApply();
        }

        private void OnAdaptiveSettingChanged(bool enabled)
        {
            if (_disposed)
            {
                return;
            }

            _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                if (_disposed)
                {
                    return;
                }

                if (!enabled)
                {
                    ClearAdaptiveState();
                }
                else
                {
                    EvaluateAndApply();
                }
            }));
        }

        private void OnGapFixSettingChanged(bool enabled)
        {
            if (_disposed)
            {
                return;
            }

            _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                if (_disposed)
                {
                    return;
                }

                if (!enabled)
                {
                    RestoreOriginalWindowHeight();
                }
                else
                {
                    EvaluateAndApply();
                }
            }));
        }

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            Dispose();
        }

        private void SnapshotOriginalResources()
        {
            _originalDefaultBackgroundBrush = _window.TryFindResource("TopBarDefaultBackgroundBrush") as Brush
                ?? _backgroundBorder?.Background;

            foreach (string key in AdaptiveResourceKeys)
            {
                object? val = _window.Resources[key] ?? _window.TryFindResource(key);
                if (val is Brush brush)
                {
                    _originalResourceBrushes[key] = brush;
                    if (brush is SolidColorBrush scb)
                    {
                        _originalResourceColors[key] = scb.Color;
                    }
                }
            }
        }

        private void HookBackgroundGuard()
        {
            if (_backgroundBorder == null)
            {
                return;
            }

            _borderBackgroundDescriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                Border.BackgroundProperty, typeof(Border));

            _borderBackgroundChangedHandler = (_, _) =>
            {
                if (_disposed || _isSettingBackground || !_isMatched)
                {
                    return;
                }

                if (!IsEligibleForAdaptiveColor())
                {
                    _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                    {
                        if (!_disposed && !IsEligibleForAdaptiveColor())
                        {
                            ClearAdaptiveState();
                        }
                    }));
                    return;
                }

                if (!ReferenceEquals(_backgroundBorder.Background, _adaptiveBackgroundBrush))
                {
                    _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                    {
                        if (!_disposed && _isMatched && IsEligibleForAdaptiveColor() && _backgroundBorder != null &&
                            !ReferenceEquals(_backgroundBorder.Background, _adaptiveBackgroundBrush))
                        {
                            SuppressSmartBackdropIfActive();
                            SetBorderBackgroundGuarded(_adaptiveBackgroundBrush);
                            _backgroundBorder.Opacity = 1.0;
                        }
                    }));
                }
            };

            _borderBackgroundDescriptor?.AddValueChanged(_backgroundBorder, _borderBackgroundChangedHandler);
        }

        private void HookDynamicItemsControls()
        {
            if (_inputRoot == null)
            {
                return;
            }

            foreach (string name in new[] { "AiRecommendItemsControl", "ShortcutItemsControl", "StatusNotificationItemsControl" })
            {
                if (_window.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(_window) is ItemsControl ic)
                {
                    _dynamicItemsControls.Add(ic);
                    ic.ItemContainerGenerator.StatusChanged += OnItemsContainerStatusChanged;
                }
            }
        }

        private void OnItemsContainerStatusChanged(object? sender, EventArgs e)
        {
            if (_disposed || !_isMatched || !_useDarkForeground)
            {
                return;
            }

            if (sender is ItemContainerGenerator gen && gen.Status == GeneratorStatus.ContainersGenerated)
            {
                _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (!_disposed && _isMatched && _useDarkForeground && _inputRoot != null)
                    {
                        ApplyForegroundToVisualTree(_inputRoot, true);
                    }
                }));
            }
        }

        private void InstallWinEventHooks()
        {
            _winEventDelegate = OnWinEventReceived;
            _hookForeground = SetWinEventHook(
                EventSystemForeground,
                EventSystemMinimizeEnd,
                IntPtr.Zero,
                _winEventDelegate,
                0,
                0,
                WinEventOutOfContext | WinEventSkipOwnProcess);

            _hookShowHide = SetWinEventHook(
                EventObjectDestroy,
                EventObjectHide,
                IntPtr.Zero,
                _winEventDelegate,
                0,
                0,
                WinEventOutOfContext | WinEventSkipOwnProcess);

            _hookLocation = SetWinEventHook(
                EventObjectLocationChange,
                EventObjectLocationChange,
                IntPtr.Zero,
                _winEventDelegate,
                0,
                0,
                WinEventOutOfContext | WinEventSkipOwnProcess);
        }

        private void OnWinEventReceived(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint idEventThread,
            uint dwmsEventTime)
        {
            if (_disposed)
            {
                return;
            }

            if (eventType == EventSystemMinimizeStart)
            {
                if (hwnd != IntPtr.Zero)
                {
                    _minimizingExclusions[hwnd] = Environment.TickCount64 + 450;
                    if (hwnd == _lastMatchedHwnd)
                    {
                        EvaluateAndApply();
                        ScheduleCoalescedEvaluate(startBurst: true);
                        return;
                    }
                }
            }
            else if (eventType == EventSystemMinimizeEnd)
            {
                if (hwnd != IntPtr.Zero && !IsIconic(hwnd))
                {
                    _minimizingExclusions.Remove(hwnd);
                }
                EvaluateAndApply();
                ScheduleCoalescedEvaluate(startBurst: true);
                return;
            }
            else if (eventType == EventSystemForeground)
            {
                if (hwnd != IntPtr.Zero && !IsIconic(hwnd))
                {
                    _minimizingExclusions.Remove(hwnd);
                }
                if (AdaptiveSettingsStore.IsFastSettingsResponseEnabled && IsPopupForegroundWindow(hwnd))
                {
                    ScheduleCoalescedEvaluate(startBurst: true);
                    return;
                }
                EvaluateAndApply();
                ScheduleCoalescedEvaluate(startBurst: true);
                return;
            }
            else if (eventType == EventObjectDestroy || eventType == EventObjectHide)
            {
                if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero)
                {
                    return;
                }

                if (hwnd == _hwnd)
                {
                    ScheduleCoalescedEvaluate(startBurst: true);
                    return;
                }

                if (hwnd != _lastMatchedHwnd)
                {
                    if (GetBoolField(_fixedFullscreenHiddenField))
                    {
                        ScheduleCoalescedEvaluate();
                    }
                    return;
                }
                _minimizingExclusions[hwnd] = Environment.TickCount64 + 350;
                EvaluateAndApply();
                ScheduleCoalescedEvaluate(startBurst: true);
                return;
            }
            else if (eventType == EventObjectShow)
            {
                if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero)
                {
                    return;
                }

                if (GetBoolField(_fixedFullscreenHiddenField))
                {
                    ScheduleCoalescedEvaluate();
                }
            }
            else if (eventType == EventObjectLocationChange)
            {
                if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero)
                {
                    return;
                }

                // Ignore noisy background window moves unless it's the foreground or currently matched window.
                if (hwnd != _lastMatchedHwnd && hwnd != GetForegroundWindow())
                {
                    return;
                }
            }

            ScheduleCoalescedEvaluate(startBurst: true);
        }

        private void ScheduleCoalescedEvaluate(bool startBurst = false)
        {
            if (_disposed)
            {
                return;
            }

            if (!_coalesceTimer.IsEnabled)
            {
                _coalesceTimer.Start();
            }

            if (startBurst)
            {
                _burstRemaining = 5;
                if (!_burstTimer.IsEnabled)
                {
                    _burstTimer.Start();
                }
            }
        }

        private IntPtr EnsureHwnd()
        {
            if (_hwnd == IntPtr.Zero)
            {
                _hwnd = new WindowInteropHelper(_window).Handle;
            }
            return _hwnd;
        }

        private bool IsEligibleForAdaptiveColor()
        {
            if (!AdaptiveSettingsStore.IsEnabled)
            {
                return false;
            }

            if (!_window.IsLoaded || !_window.IsVisible || _backgroundBorder == null)
            {
                return false;
            }

            if (GetIntField(_toolbarModeField, 0) != 0) // 0 = Always (Fixed mode)
            {
                return false;
            }

            if (GetBoolField(_fixedFullscreenHiddenField) ||
                GetBoolField(_displayTransitionHiddenField) ||
                GetBoolField(_closingTopologyField) ||
                GetIntField(_barModePhaseField, 0) != 0)
            {
                return false;
            }

            return true;
        }

        private bool IsPopupOpen()
        {
            if (_isAnyPopupOpenProp?.GetValue(_window) is bool open && open)
            {
                return true;
            }
            return GetBoolField(_toolbarModePopupOpenField);
        }

        private void RecoverFalseFixedFullscreenHideIfNeeded()
        {
            if (_disposed ||
                _applyFixedFullscreenMethod == null ||
                GetIntField(_toolbarModeField, 0) != 0 ||
                !GetBoolField(_fixedFullscreenHiddenField) ||
                GetBoolField(_displayTransitionHiddenField) ||
                GetBoolField(_closingTopologyField) ||
                GetIntField(_barModePhaseField, 0) != 0)
            {
                return;
            }

            IntPtr hwnd = EnsureHwnd();
            if (hwnd == IntPtr.Zero || NativeWindowSampler.HasRealFullscreenWindow(hwnd))
            {
                return;
            }

            long now = Environment.TickCount64;
            if (now - _lastFullscreenRecoverTick < 150)
            {
                return;
            }

            _lastFullscreenRecoverTick = now;
            try
            {
                _applyFixedFullscreenMethod.Invoke(_window, new object[] { false });
                DiagnosticLog.Info("Recovered TopBar visibility from screen-recorder / non-fullscreen overlay.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"ApplyFixedFullscreenVisibility(false) failed: {ex.Message}");
            }
        }

        private void EvaluateAndApply()
        {
            if (_disposed)
            {
                return;
            }

            RecoverFalseFixedFullscreenHideIfNeeded();

            if (!IsEligibleForAdaptiveColor())
            {
                ClearAdaptiveState();
                return;
            }

            IntPtr topBarHwnd = EnsureHwnd();
            if (topBarHwnd == IntPtr.Zero)
            {
                ClearAdaptiveState();
                return;
            }

            bool allowPixelSample = !IsPopupOpen() &&
                !(AdaptiveSettingsStore.IsFastSettingsResponseEnabled && IsPopupForegroundWindow(GetForegroundWindow()));
            var excluded = GetActiveMinimizingExclusions();
            WindowSampleResult sample = NativeWindowSampler.SampleTargetMonitor(topBarHwnd, allowPixelSample, excluded);

            if (sample.Kind != WindowMatchKind.MaximizedWindow)
            {
                ClearAdaptiveState();
                return;
            }

            bool hwndChanged = _lastMatchedHwnd != sample.Hwnd;
            _lastMatchedHwnd = sample.Hwnd;

            Color targetColor;
            if (sample.SampledColor.HasValue)
            {
                targetColor = sample.SampledColor.Value;
            }
            else if (_isMatched && _currentTargetColor.HasValue)
            {
                targetColor = _currentTargetColor.Value;
            }
            else
            {
                return;
            }

            ApplyMatchedColor(targetColor, hwndChanged ? sample.ProcessName : null);
            EnsureAdaptiveBottomAlignment(in sample);
        }

        private void ApplyMatchedColor(Color targetColor, string? matchedProcessName = null)
        {
            if (_backgroundBorder == null)
            {
                return;
            }

            bool wasMatched = _isMatched;
            _isMatched = true;

            if (GetBoolField(_nativeFixedVisualActiveField))
            {
                _restoreWpfVisualMethod?.Invoke(_window, null);
            }

            SuppressSmartBackdropIfActive();
            _backgroundBorder.Opacity = 1.0;

            bool colorChanged = !_currentTargetColor.HasValue ||
                Math.Abs(_currentTargetColor.Value.R - targetColor.R) > 2 ||
                Math.Abs(_currentTargetColor.Value.G - targetColor.G) > 2 ||
                Math.Abs(_currentTargetColor.Value.B - targetColor.B) > 2;

            if (!wasMatched || colorChanged || !ReferenceEquals(_backgroundBorder.Background, _adaptiveBackgroundBrush))
            {
                _currentTargetColor = targetColor;
                SetBorderBackgroundGuarded(_adaptiveBackgroundBrush);

                if (!wasMatched)
                {
                    _adaptiveBackgroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    _adaptiveBackgroundBrush.Color = targetColor;
                }
                else
                {
                    var anim = new ColorAnimation(targetColor, ColorTransitionDuration)
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    _adaptiveBackgroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                }

                string procTag = string.IsNullOrEmpty(matchedProcessName) ? "" : $" proc={matchedProcessName}";
                DiagnosticLog.Info($"Matched maximized window color -> #{targetColor.R:X2}{targetColor.G:X2}{targetColor.B:X2}{procTag}");
            }

            // Compute relative luminance and update foreground contrast with hysteresis.
            double lum = (0.2126 * targetColor.R + 0.7152 * targetColor.G + 0.0722 * targetColor.B) / 255.0;
            bool nextDark = !wasMatched
                ? lum >= InitialLuminanceThreshold
                : (_useDarkForeground ? lum > LightForegroundLuminanceEnter : lum >= DarkForegroundLuminanceEnter);

            if (!wasMatched || nextDark != _useDarkForeground)
            {
                _useDarkForeground = nextDark;
                ApplyForegroundTheme(nextDark);
                _lastDarkForegroundTreeSyncTick = Environment.TickCount64;
            }
            else if (_useDarkForeground && _inputRoot != null)
            {
                long nowTick = Environment.TickCount64;
                if (nowTick - _lastDarkForegroundTreeSyncTick >= DarkForegroundTreeSyncIntervalMs)
                {
                    _lastDarkForegroundTreeSyncTick = nowTick;
                    // Periodically catch late-updated icons/text on light backgrounds; dynamic item
                    // containers already sync immediately in OnItemsContainerStatusChanged.
                    ApplyForegroundToVisualTree(_inputRoot, true);
                }
            }
        }

        private void ClearAdaptiveState()
        {
            RestoreOriginalWindowHeight();

            if (!_isMatched && !_currentTargetColor.HasValue)
            {
                return;
            }

            _isMatched = false;
            _lastMatchedHwnd = IntPtr.Zero;
            _currentTargetColor = null;
            _lastDarkForegroundTreeSyncTick = 0;

            if (_useDarkForeground)
            {
                _useDarkForeground = false;
                ApplyForegroundTheme(false);
            }

            _adaptiveBackgroundBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);

            if (_backgroundBorder != null)
            {
                SetBorderBackgroundGuarded(_originalDefaultBackgroundBrush);
            }

            _applyBackgroundStateMethod?.Invoke(_window, null);

            if (GetBoolField(_acrylicEnabledField) &&
                GetIntField(_toolbarModeField, 0) == 0 &&
                !GetBoolField(_fixedFullscreenHiddenField) &&
                !GetBoolField(_displayTransitionHiddenField))
            {
                _syncSmartBackdropMethod?.Invoke(_window, new object[] { true });
            }

            DiagnosticLog.Info("Reverted TopBar to default appearance.");
        }

        private void SuppressSmartBackdropIfActive()
        {
            if (GetBoolField(_smartBackdropActiveField))
            {
                object? host = _desktopLayerHostField?.GetValue(_window);
                _hideSmartBackdropMethod?.Invoke(host, null);
                _smartBackdropActiveField?.SetValue(_window, false);
            }
        }

        private void SetBorderBackgroundGuarded(Brush? brush)
        {
            if (_backgroundBorder == null)
            {
                return;
            }

            bool prev = _isSettingBackground;
            _isSettingBackground = true;
            try
            {
                _backgroundBorder.Background = brush;
            }
            finally
            {
                _isSettingBackground = prev;
            }
        }

        private void ApplyForegroundTheme(bool useDarkForeground)
        {
            _svgThemer.SetDarkForeground(useDarkForeground);
            UpdateWindowResources(useDarkForeground);

            if (_inputRoot != null)
            {
                ApplyForegroundToVisualTree(_inputRoot, useDarkForeground);
            }
        }

        private void UpdateWindowResources(bool useDarkForeground)
        {
            SetBrushResource("TopBarForegroundBrush", useDarkForeground, DarkInkColor);
            SetBrushResource("TopBarSecondaryForegroundBrush", useDarkForeground, Color.FromArgb(0xB3, DarkInkColor.R, DarkInkColor.G, DarkInkColor.B));
            SetBrushResource("TopBarHoverBrush", useDarkForeground, Color.FromArgb(0x14, 0, 0, 0));
            SetBrushResource("TopBarPressedBrush", useDarkForeground, Color.FromArgb(0x24, 0, 0, 0));
            SetBrushResource("TopBarSelectedBrush", useDarkForeground, Color.FromArgb(0x18, 0, 0, 0));
            SetBrushResource("TopBarAiRecommendSelectedBrush", useDarkForeground, Color.FromArgb(0x22, 0x3D, 0x7F, 0xFF));
        }

        private void SetBrushResource(string key, bool useDarkForeground, Color darkColor)
        {
            if (!_originalResourceColors.TryGetValue(key, out Color originalColor))
            {
                return;
            }

            Color targetColor = useDarkForeground ? darkColor : originalColor;
            object? current = _window.Resources[key] ?? (_originalResourceBrushes.TryGetValue(key, out var orig) ? orig : null);

            if (current is SolidColorBrush scb && !scb.IsFrozen)
            {
                scb.Color = targetColor;
                return;
            }

            var replacement = new SolidColorBrush(targetColor);
            _window.Resources[key] = replacement;
        }

        private void ApplyForegroundToVisualTree(DependencyObject root, bool useDarkForeground)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);

                if (child is Popup)
                {
                    continue;
                }

                if (_svgThemer.IsSvgViewbox(child))
                {
                    _svgThemer.ApplyToSvg(child, useDarkForeground);
                }
                else if (child is TextBlock tb)
                {
                    ApplyToTextBlock(tb, useDarkForeground);
                }
                else if (child is Border border && border.OpacityMask != null)
                {
                    ApplyToMaskedBorder(border, useDarkForeground);
                }
                else if (child is Shape shape)
                {
                    ApplyToShape(shape, useDarkForeground);
                }

                ApplyForegroundToVisualTree(child, useDarkForeground);
            }
        }

        private void ApplyToTextBlock(TextBlock tb, bool useDarkForeground)
        {
            if (!_originalTextForegrounds.TryGetValue(tb, out Brush? orig))
            {
                object localVal = tb.ReadLocalValue(TextBlock.ForegroundProperty);
                orig = localVal == DependencyProperty.UnsetValue ? null : tb.Foreground;
                _originalTextForegrounds.Add(tb, orig);
            }

            if (useDarkForeground)
            {
                bool isSemiTransparent = tb.Name == "BatteryPercentText" ||
                    (orig is SolidColorBrush scb && scb.Color.A is > 0 and < 250);
                Brush target = isSemiTransparent ? _darkBatteryTextBrush : _darkTextBrush;
                if (!ReferenceEquals(tb.Foreground, target))
                {
                    tb.Foreground = target;
                }
            }
            else
            {
                if (orig == null)
                {
                    if (tb.ReadLocalValue(TextBlock.ForegroundProperty) != DependencyProperty.UnsetValue)
                    {
                        tb.ClearValue(TextBlock.ForegroundProperty);
                    }
                }
                else if (!ReferenceEquals(tb.Foreground, orig))
                {
                    tb.Foreground = orig;
                }
            }
        }

        private void ApplyToMaskedBorder(Border border, bool useDarkForeground)
        {
            if (!_originalMaskBackgrounds.TryGetValue(border, out Brush? orig))
            {
                object localVal = border.ReadLocalValue(Border.BackgroundProperty);
                orig = localVal == DependencyProperty.UnsetValue ? null : border.Background;
                _originalMaskBackgrounds.Add(border, orig);
            }

            if (useDarkForeground)
            {
                if (!ReferenceEquals(border.Background, _darkTextBrush))
                {
                    border.Background = _darkTextBrush;
                }
            }
            else if (orig != null && !ReferenceEquals(border.Background, orig))
            {
                border.Background = orig;
            }
        }

        private void ApplyToShape(Shape shape, bool useDarkForeground)
        {
            if (string.Equals(shape.Name, "HonorUpdateBadge", StringComparison.Ordinal))
            {
                return;
            }

            if (!_originalShapeBrushes.TryGetValue(shape, out var orig))
            {
                orig = new ShapeBrushPair(shape.Fill, shape.Stroke);
                _originalShapeBrushes.Add(shape, orig);
            }

            if (useDarkForeground)
            {
                if (IsMonochromeBrush(orig.Fill, out byte fillAlpha))
                {
                    shape.Fill = fillAlpha >= 250
                        ? _darkTextBrush
                        : CreateAlphaBrush(DarkInkColor, fillAlpha);
                }

                if (IsMonochromeBrush(orig.Stroke, out byte strokeAlpha))
                {
                    shape.Stroke = strokeAlpha >= 250
                        ? _darkTextBrush
                        : CreateAlphaBrush(DarkInkColor, strokeAlpha);
                }
            }
            else
            {
                if (!ReferenceEquals(shape.Fill, orig.Fill))
                {
                    shape.Fill = orig.Fill;
                }
                if (!ReferenceEquals(shape.Stroke, orig.Stroke))
                {
                    shape.Stroke = orig.Stroke;
                }
            }
        }

        private static bool IsMonochromeBrush(Brush? brush, out byte alpha)
        {
            alpha = 255;
            if (brush is not SolidColorBrush scb || scb.Color.A == 0)
            {
                return false;
            }

            Color c = scb.Color;
            alpha = c.A;
            bool isWhite = c.R >= 230 && c.G >= 230 && c.B >= 230;
            bool isBlack = c.R <= 35 && c.G <= 35 && c.B <= 35;
            return isWhite || isBlack;
        }

        private static Brush CreateAlphaBrush(Color rgb, byte alpha)
        {
            var b = new SolidColorBrush(Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B));
            b.Freeze();
            return b;
        }

        private void CaptureBaseLayoutSnapshot()
        {
            double dip = DefaultTopBarHeightDip;
            try
            {
                object? mgr = _appBarManagerField?.GetValue(_window);
                if (mgr != null)
                {
                    object? val = mgr.GetType().GetField("_heightDip", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(mgr);
                    if (val != null)
                    {
                        double mgrDip = Convert.ToDouble(val);
                        if (mgrDip > 16.0 && mgrDip < 96.0)
                        {
                            dip = mgrDip;
                        }
                    }
                }
            }
            catch
            {
            }

            if (dip == DefaultTopBarHeightDip)
            {
                if (!double.IsNaN(_window.Height) && _window.Height > 16.0 && _window.Height < 96.0)
                {
                    dip = _window.Height;
                }
                else if (_window.ActualHeight > 16.0 && _window.ActualHeight < 96.0)
                {
                    dip = _window.ActualHeight;
                }
            }

            _baseWindowHeightDip = dip;
        }

        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_disposed || _isSettingWindowSize)
            {
                return;
            }

            if (_isMatched)
            {
                SuppressSmartBackdropIfActive();
                ScheduleCoalescedEvaluate();
            }
        }

        private void EnsureAdaptiveBottomAlignment(in WindowSampleResult sample)
        {
            if (_disposed || _window == null)
            {
                return;
            }

            if (!AdaptiveSettingsStore.IsGapFixEnabled)
            {
                RestoreOriginalWindowHeight();
                return;
            }

            double safeScale = sample.DpiScale > 0 ? sample.DpiScale : 1.0;
            int baseHeightPx = GetBaseTopBarHeightPx(safeScale);
            int baseBottomPx = sample.MonitorTopPx + baseHeightPx;
            int maxBridgeGapPx = Math.Max(4, (int)Math.Round(3.0 * safeScale));
            int neededBottomPx = Math.Max(sample.WorkTopPx, sample.MatchedFrameTopPx);
            int desiredBottomPx = Math.Clamp(neededBottomPx, baseBottomPx, baseBottomPx + maxBridgeGapPx);
            int desiredHeightPx = Math.Max(baseHeightPx, desiredBottomPx - sample.MonitorTopPx);

            _lastDpiScale = safeScale;
            _appliedBaseHeightPx = baseHeightPx;
            if (sample.TopBarWidthPx > 0)
            {
                _lastAppliedWidthPx = sample.TopBarWidthPx;
            }

            if (desiredHeightPx <= baseHeightPx)
            {
                if (_isHeightExtended || sample.TopBarBottomPx > baseBottomPx)
                {
                    RestoreOriginalWindowHeight(baseHeightPx, sample.TopBarWidthPx);
                }
                return;
            }

            if (_isHeightExtended && _appliedHeightPx == desiredHeightPx && sample.TopBarBottomPx == desiredBottomPx)
            {
                return;
            }

            double desiredHeightDip = desiredHeightPx / safeScale;
            int widthPx = sample.TopBarWidthPx > 0 ? sample.TopBarWidthPx : _lastAppliedWidthPx;
            ApplyExtendedWindowHeight(desiredHeightDip, desiredHeightPx, widthPx);
            DiagnosticLog.Info($"Extended TopBar heightPx={desiredHeightPx} (base={baseHeightPx}, workTop={sample.WorkTopPx}, frameTop={sample.MatchedFrameTopPx}, prevBottom={sample.TopBarBottomPx}).");
        }

        private void ApplyExtendedWindowHeight(double desiredHeightDip, int desiredHeightPx, int widthPx)
        {
            if (!_isHeightExtended)
            {
                _origWindowHeight = !double.IsNaN(_window.Height) && _window.Height > 16.0 ? _window.Height : _baseWindowHeightDip;
                _origWindowMaxHeight = _window.MaxHeight;

                if (_backgroundBorder != null)
                {
                    _origBorderHeight = _backgroundBorder.Height;
                    _origBorderVerticalAlignment = _backgroundBorder.VerticalAlignment;
                    _origBorderMargin = _backgroundBorder.Margin;
                }

                if (_inputRoot != null)
                {
                    _origInputHeight = _inputRoot.Height;
                    _origInputVerticalAlignment = _inputRoot.VerticalAlignment;
                }

                _rootContent ??= _window.Content as FrameworkElement;
                if (_rootContent != null)
                {
                    _origRootHeight = _rootContent.Height;
                    _origRootMaxHeight = _rootContent.MaxHeight;
                    _origRootVerticalAlignment = _rootContent.VerticalAlignment;
                }
            }

            _isSettingWindowSize = true;
            try
            {
                if (_inputRoot != null)
                {
                    double pinnedDip = !double.IsNaN(_origInputHeight) && _origInputHeight > 0
                        ? _origInputHeight
                        : _baseWindowHeightDip;
                    _inputRoot.Height = pinnedDip;
                    _inputRoot.VerticalAlignment = VerticalAlignment.Top;
                }

                if (_rootContent != null)
                {
                    if (_rootContent.MaxHeight < desiredHeightDip)
                    {
                        _rootContent.MaxHeight = desiredHeightDip + 8.0;
                    }
                    if (!double.IsNaN(_rootContent.Height))
                    {
                        _rootContent.Height = double.NaN;
                    }
                    _rootContent.VerticalAlignment = VerticalAlignment.Stretch;
                }

                if (_backgroundBorder != null)
                {
                    _backgroundBorder.Height = double.NaN;
                    _backgroundBorder.VerticalAlignment = VerticalAlignment.Stretch;
                    if (_origBorderMargin.Bottom != 0)
                    {
                        _backgroundBorder.Margin = new Thickness(_origBorderMargin.Left, _origBorderMargin.Top, _origBorderMargin.Right, 0);
                    }
                }

                if (_window.MaxHeight < desiredHeightDip)
                {
                    _window.MaxHeight = desiredHeightDip + 8.0;
                }

                _window.Height = desiredHeightDip;

                if (_hwnd != IntPtr.Zero && IsWindow(_hwnd) && widthPx > 0)
                {
                    SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, widthPx, desiredHeightPx, SwpNomove | SwpNozorder | SwpNoactivate);
                }

                _isHeightExtended = true;
                _appliedHeightPx = desiredHeightPx;
            }
            finally
            {
                _isSettingWindowSize = false;
            }

            SuppressSmartBackdropIfActive();
            if (_backgroundBorder != null && _isMatched)
            {
                if (!ReferenceEquals(_backgroundBorder.Background, _adaptiveBackgroundBrush))
                {
                    SetBorderBackgroundGuarded(_adaptiveBackgroundBrush);
                }
                _backgroundBorder.Opacity = 1.0;
            }
        }

        private void RestoreOriginalWindowHeight(int? explicitBaseHeightPx = null, int? explicitWidthPx = null)
        {
            if (!_isHeightExtended)
            {
                return;
            }

            _isHeightExtended = false;
            _appliedHeightPx = 0;

            _isSettingWindowSize = true;
            try
            {
                if (_backgroundBorder != null)
                {
                    _backgroundBorder.Height = _origBorderHeight;
                    _backgroundBorder.VerticalAlignment = _origBorderVerticalAlignment;
                    _backgroundBorder.Margin = _origBorderMargin;
                }

                if (_inputRoot != null)
                {
                    _inputRoot.Height = _origInputHeight;
                    _inputRoot.VerticalAlignment = _origInputVerticalAlignment;
                }

                if (_rootContent != null)
                {
                    _rootContent.Height = _origRootHeight;
                    _rootContent.MaxHeight = _origRootMaxHeight;
                    _rootContent.VerticalAlignment = _origRootVerticalAlignment;
                }

                _window.MaxHeight = _origWindowMaxHeight;
                _window.Height = !double.IsNaN(_origWindowHeight) && _origWindowHeight > 0 ? _origWindowHeight : _baseWindowHeightDip;

                int baseHeightPx = explicitBaseHeightPx ?? (_appliedBaseHeightPx > 0 ? _appliedBaseHeightPx : GetBaseTopBarHeightPx(_lastDpiScale));
                int widthPx = explicitWidthPx is > 0 ? explicitWidthPx.Value : _lastAppliedWidthPx;
                if (_hwnd != IntPtr.Zero && IsWindow(_hwnd) && widthPx > 0 && baseHeightPx > 0)
                {
                    SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, widthPx, baseHeightPx, SwpNomove | SwpNozorder | SwpNoactivate);
                }
            }
            catch
            {
            }
            finally
            {
                _isSettingWindowSize = false;
            }

            DiagnosticLog.Info("Restored TopBar base height.");
        }

        private int GetBaseTopBarHeightPx(double dpiScale)
        {
            if (_getTopBarHeightPixelsMethod != null)
            {
                try
                {
                    if (_getTopBarHeightPixelsMethod.Invoke(_window, null) is int px && px >= 20 && px <= 128)
                    {
                        return px;
                    }
                }
                catch
                {
                }
            }

            double safeScale = dpiScale > 0 ? dpiScale : 1.0;
            return Math.Max(20, (int)Math.Round(_baseWindowHeightDip * safeScale));
        }

        private bool GetBoolField(FieldInfo? field)
        {
            return field?.GetValue(_window) is bool b && b;
        }

        private int GetIntField(FieldInfo? field, int fallback)
        {
            object? v = field?.GetValue(_window);
            if (v == null)
            {
                return fallback;
            }
            return Convert.ToInt32(v);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pollTimer.Stop();
            _coalesceTimer.Stop();
            _burstTimer.Stop();
            _windowDragController.Dispose();
            _uiEnhancer.Dispose();
            _window.SizeChanged -= OnWindowSizeChanged;
            AdaptiveSettingsStore.EnabledChanged -= OnAdaptiveSettingChanged;
            AdaptiveSettingsStore.GapFixChanged -= OnGapFixSettingChanged;

            if (_hookForeground != IntPtr.Zero)
            {
                UnhookWinEvent(_hookForeground);
                _hookForeground = IntPtr.Zero;
            }

            if (_hookShowHide != IntPtr.Zero)
            {
                UnhookWinEvent(_hookShowHide);
                _hookShowHide = IntPtr.Zero;
            }

            if (_hookLocation != IntPtr.Zero)
            {
                UnhookWinEvent(_hookLocation);
                _hookLocation = IntPtr.Zero;
            }

            if (_backgroundBorder != null && _borderBackgroundChangedHandler != null)
            {
                _borderBackgroundDescriptor?.RemoveValueChanged(_backgroundBorder, _borderBackgroundChangedHandler);
            }

            foreach (var ic in _dynamicItemsControls)
            {
                ic.ItemContainerGenerator.StatusChanged -= OnItemsContainerStatusChanged;
            }

            ClearAdaptiveState();
        }

        private IReadOnlyCollection<IntPtr>? GetActiveMinimizingExclusions()
        {
            if (_minimizingExclusions.Count == 0)
            {
                return null;
            }

            long now = Environment.TickCount64;
            _activeExclusionBuffer.Clear();
            List<IntPtr>? expired = null;

            foreach (var kv in _minimizingExclusions)
            {
                if (kv.Value <= now)
                {
                    expired ??= new List<IntPtr>(2);
                    expired.Add(kv.Key);
                }
                else
                {
                    _activeExclusionBuffer.Add(kv.Key);
                }
            }

            if (expired != null)
            {
                for (int i = 0; i < expired.Count; i++)
                {
                    _minimizingExclusions.Remove(expired[i]);
                }
            }

            return _activeExclusionBuffer.Count > 0 ? _activeExclusionBuffer : null;
        }

        private static bool IsPopupForegroundWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)Environment.ProcessId)
            {
                return true;
            }

            var sb = new StringBuilder(128);
            if (GetClassName(hwnd, sb, sb.Capacity) > 0)
            {
                string cls = sb.ToString();
                if (cls.StartsWith("HwndWrapper[ControlCenterTrayUI", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            sb.Clear();
            if (GetWindowText(hwnd, sb, sb.Capacity) > 0)
            {
                string title = sb.ToString();
                if (string.Equals(title, "AiDesktopMainWindow", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private delegate void WinEventDelegate(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint idEventThread,
            uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc,
            uint idProcess,
            uint idThread,
            uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint uFlags);
    }
}

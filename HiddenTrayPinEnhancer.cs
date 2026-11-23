using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TopBarAdaptiveColor
{
    /// <summary>
    /// Request payload passed to StatusNotificationTrayRelay when interacting with a custom pinned tray icon.
    /// </summary>
    internal readonly record struct CustomTrayClickRequest(
        string? ExecutablePath,
        string? ProcessName,
        string? DisplayName,
        uint? KnownUid,
        Guid? IconGuid,
        IntPtr KnownHwnd,
        uint? KnownCallbackMessage,
        bool? KnownIsNotifyIconV4,
        int ScreenX,
        int ScreenY,
        bool IsRightClick,
        bool IsDoubleClick);

    internal delegate Task<bool> CustomTrayClickHandler(CustomTrayClickRequest request);

    /// <summary>
    /// Allows dragging any hidden system tray icon from Windows' overflow popup onto the TopBar
    /// fixed status area, with persistence across restarts, live icon/tooltip/exit updates,
    /// adaptive light/dark theming (preserving colored icons), left/double-click activation,
    /// native right-click relay coordination, and Shift+RightClick / drag-off unpinning.
    /// </summary>
    internal sealed class HiddenTrayPinEnhancer : IDisposable
    {
        private const string CustomItemsControlName = "CustomPinnedTrayItemsControl";
        private const int LiveRefreshIntervalMs = 2000;

        private static CustomTrayClickHandler? _externalTrayRelay;

        private readonly Window _topBarWindow;
        private readonly ItemsControl? _statusItemsControl;
        private readonly Button? _trayButton;
        private readonly Border? _backgroundBorder;
        private readonly TextBlock? _dateTimeText;
        private readonly MethodInfo? _openRemoveMenuMethod;
        private readonly MethodInfo? _disableStatusAppMethod;
        private readonly MethodInfo? _closePopupsExceptMethod;
        private readonly object? _popupSurfaceNone;
        private readonly bool _origAllowDrop;

        private readonly ObservableCollection<PinnedTrayItemViewModel> _visibleItems = new();
        private ItemsControl? _customItemsControl;
        private Border? _dropInsertionIndicator;
        private StackPanel? _wrapperPanel;
        private Panel? _hostStackPanel;

        private readonly DispatcherTimer _liveRefreshTimer;
        private readonly DragEventHandler _previewDragOverHandler;
        private readonly DragEventHandler _previewDropHandler;
        private readonly DragEventHandler _previewDragLeaveHandler;
        private readonly MouseButtonEventHandler _nativeDownHandler;
        private readonly MouseEventHandler _nativeMoveHandler;
        private readonly MouseButtonEventHandler _nativeUpHandler;
        private readonly MouseEventHandler _nativeLostCaptureHandler;

        private DependencyPropertyDescriptor? _dateForegroundDescriptor;
        private DependencyPropertyDescriptor? _bgBorderDescriptor;
        private EventHandler? _themeChangedHandler;

        private bool _useDarkForeground;
        private bool _isDropHighlighted;
        private bool _trayArrowHighlighted;
        private Brush? _trayArrowOrigBackground;
        private int _refreshRunning;
        private int _pendingRefresh;
        private bool _disposed;

        // In-bar drag reorder / drag-to-arrow or drag-off-to-unpin state
        private PinnedTrayItemViewModel? _barDragCandidate;
        private Button? _barDragButton;
        private POINT _barDragStartPx;
        private bool _barDragging;
        private bool _suppressNextClick;

        // Native StatusNotificationItemsControl drag-to-arrow & click-rescue state
        private string? _nativeDragAppId;
        private string? _nativeDragDisplayName;
        private string? _nativeDragLaunchPath;
        private IReadOnlyList<string>? _nativeDragProcNames;
        private ImageSource? _nativeDragIcon;
        private Button? _nativeDragButton;
        private POINT _nativeDragStartPx;
        private bool _nativeBarDragging;
        private bool _nativeSuppressNextClick;
        private HashSet<uint>? _nativeClickPids;
        private IntPtr _nativeClickPrevFg;
        private HashSet<IntPtr>? _nativeClickPrevVisible;
        private HashSet<IntPtr>? _nativeClickPrevRestored;
        private long _clickSequenceGen;

        internal static void RegisterCustomTrayRelay(CustomTrayClickHandler? handler)
        {
            Volatile.Write(ref _externalTrayRelay, handler);
        }

        public HiddenTrayPinEnhancer(Window topBarWindow)
        {
            _topBarWindow = topBarWindow ?? throw new ArgumentNullException(nameof(topBarWindow));
            _origAllowDrop = topBarWindow.AllowDrop;

            const BindingFlags instFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            Type twType = topBarWindow.GetType();
            _statusItemsControl = twType.GetField("StatusNotificationItemsControl", instFlags)?.GetValue(topBarWindow) as ItemsControl;
            _trayButton = twType.GetField("TrayButton", instFlags)?.GetValue(topBarWindow) as Button;
            _backgroundBorder = twType.GetField("TopBarBackgroundBorder", instFlags)?.GetValue(topBarWindow) as Border;
            _dateTimeText = twType.GetField("DateTimeText", instFlags)?.GetValue(topBarWindow) as TextBlock;
            _openRemoveMenuMethod = twType.GetMethod("OpenBarItemRemoveContextMenu", instFlags);
            _disableStatusAppMethod = twType.GetMethod("DisableStatusNotificationApp", instFlags);
            _closePopupsExceptMethod = twType.GetMethod("CloseTopBarPopupSurfacesExcept", instFlags);
            if (_closePopupsExceptMethod != null)
            {
                var ps = _closePopupsExceptMethod.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.IsEnum)
                {
                    _popupSurfaceNone = Enum.ToObject(ps[0].ParameterType, 0);
                }
            }

            _previewDragOverHandler = OnTopBarPreviewDragOver;
            _previewDropHandler = OnTopBarPreviewDrop;
            _previewDragLeaveHandler = OnTopBarPreviewDragLeave;
            _nativeDownHandler = OnNativeStatusPreviewMouseLeftButtonDown;
            _nativeMoveHandler = OnNativeStatusPreviewMouseMove;
            _nativeUpHandler = OnNativeStatusPreviewMouseLeftButtonUp;
            _nativeLostCaptureHandler = OnNativeStatusLostMouseCapture;

            _liveRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, _topBarWindow.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(LiveRefreshIntervalMs)
            };
            _liveRefreshTimer.Tick += (_, _) => QueueLiveRefresh();

            EnsureUiContainerInjected();
            AttachNativeStatusDragHandlers();
            HookThemeObservers();
            ApplyOleDropSupport();

            PinnedTrayStore.StoreChanged += OnStoreChanged;
            AdaptiveSettingsStore.HiddenTrayIconPinningChanged += OnFeatureEnabledChanged;
            _topBarWindow.Closed += OnTopBarWindowClosed;
            OverflowDragCoordinator.RegisterEnhancer(this);

            UpdateVisibilityAndTimers();
            SyncVisibleItemsFromStoreSnapshot(forceIconRebuild: true);
            QueueLiveRefresh();

            DiagnosticLog.Info($"HiddenTrayPinEnhancer attached (statusHostFound={_hostStackPanel != null}, enabled={AdaptiveSettingsStore.IsHiddenTrayIconPinningEnabled}).");
        }

        internal Window TopBarWindow => _topBarWindow;

        internal bool TryGetDropZoneRectPx(out RECT rectPx)
        {
            rectPx = default;
            if (_disposed || !AdaptiveSettingsStore.IsHiddenTrayIconPinningEnabled || !_topBarWindow.IsVisible)
            {
                return false;
            }

            IntPtr hwnd = new WindowInteropHelper(_topBarWindow).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT winRect))
            {
                return false;
            }

            double scale = GetDpiScaleForWindow(hwnd);
            int padBottom = Math.Max(14, (int)Math.Round(18.0 * scale));
            int padSide = Math.Max(6, (int)Math.Round(8.0 * scale));
            rectPx = new RECT
            {
                Left = winRect.Left - padSide,
                Top = winRect.Top - padSide,
                Right = winRect.Right + padSide,
                Bottom = winRect.Bottom + padBottom
            };
            return rectPx.Right > rectPx.Left && rectPx.Bottom > rectPx.Top;
        }

        internal void SetDropHighlight(bool active)
        {
            if (_disposed || _isDropHighlighted == active)
            {
                return;
            }

            _isDropHighlighted = active;
            if (_dropInsertionIndicator != null)
            {
                _dropInsertionIndicator.Visibility = active && AdaptiveSettingsStore.IsHiddenTrayIconPinningEnabled
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        internal void CommitDraggedOverflowCandidate(OverflowDropCandidate candidate)
        {
            if (_disposed || !AdaptiveSettingsStore.IsHiddenTrayIconPinningEnabled)
            {
                return;
            }

            if (IsAlreadyInOfficialStatusBar(candidate.ExecutablePath, candidate.ProcessName, candidate.DisplayName))
            {
                DiagnosticLog.Info($"Pinned tray drop skipped: '{candidate.DisplayName}' is already active in official StatusNotificationItemsControl.");
                return;
            }

            var entry = new PinnedTrayEntry
            {
                PinId = candidate.BuildPinId(),
                ExecutablePath = candidate.ExecutablePath ?? string.Empty,
                ProcessName = candidate.ProcessName ?? string.Empty,
                DisplayName = !string.IsNullOrWhiteSpace(candidate.DisplayName)
                    ? candidate.DisplayName
                    : Path.GetFileNameWithoutExtension(candidate.ExecutablePath) ?? "Tray App",
                RegistrySubkeyName = candidate.RegistrySubkeyName,
                NotifyIconUid = candidate.NotifyIconUid,
                IconGuid = candidate.IconGuid?.ToString("D"),
                TooltipHint = candidate.DisplayName,
                CachedIconPngBase64 = candidate.IconPngBytes is { Length: > 0 }
                    ? Convert.ToBase64String(candidate.IconPngBytes)
                    : null
            };

            PinnedTrayStore.UpsertAndPersist(entry);
            DiagnosticLog.Info(
                $"Pinned custom tray icon: pinId={entry.PinId} name='{entry.DisplayName}' exe='{entry.ExecutablePath}' uid={entry.NotifyIconUid?.ToString() ?? "<none>"}.");
        }

        private bool IsAlreadyInOfficialStatusBar(string? exePath, string? procName, string? displayName)
        {
            if (_statusItemsControl == null)
            {
                return false;
            }

            string? exeFile = string.IsNullOrWhiteSpace(exePath) ? null : Path.GetFileNameWithoutExtension(exePath);
            foreach (object? item in _statusItemsControl.Items)
            {
                if (item == null)
                {
                    continue;
                }

                Type t = item.GetType();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                string? itemLaunch = t.GetProperty("LaunchPath", flags)?.GetValue(item) as string;
                string? itemDisplay = t.GetProperty("DisplayName", flags)?.GetValue(item) as string;
                object? appDef = t.GetProperty("App", flags)?.GetValue(item);
                var appProcs = appDef?.GetType().GetProperty("ProcessNames", flags)?.GetValue(appDef) as IReadOnlyList<string>;

                if (!string.IsNullOrWhiteSpace(exePath) && !string.IsNullOrWhiteSpace(itemLaunch) &&
                    string.Equals(NormalizePath(exePath), NormalizePath(itemLaunch), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (appProcs != null)
                {
                    foreach (string p in appProcs)
                    {
                        string pBase = Path.GetFileNameWithoutExtension(p);
                        if ((!string.IsNullOrWhiteSpace(exeFile) && string.Equals(pBase, exeFile, StringComparison.OrdinalIgnoreCase)) ||
                            (!string.IsNullOrWhiteSpace(procName) && string.Equals(pBase, procName, StringComparison.OrdinalIgnoreCase)))
                        {
                            return true;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(itemDisplay) &&
                    string.Equals(displayName.Trim(), itemDisplay.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void EnsureUiContainerInjected()
        {
            if (_statusItemsControl == null || _wrapperPanel != null)
            {
                return;
            }

            DependencyObject? parentObj = VisualTreeHelper.GetParent(_statusItemsControl)
                ?? LogicalTreeHelper.GetParent(_statusItemsControl);

            if (parentObj is not Panel parentPanel)
            {
                DiagnosticLog.Warn("HiddenTrayPinEnhancer: StatusNotificationItemsControl parent panel not found.");
                return;
            }

            _hostStackPanel = parentPanel;
            int statusIdx = parentPanel.Children.IndexOf(_statusItemsControl);
            if (statusIdx < 0)
            {
                statusIdx = parentPanel.Children.Count - 1;
            }

            _customItemsControl = new ItemsControl
            {
                Name = CustomItemsControlName,
                ItemsSource = _visibleItems,
                Focusable = false,
                Margin = new Thickness(0),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                ItemsPanel = CreateHorizontalItemsPanelTemplate(),
                ItemTemplate = CreatePinnedItemDataTemplate()
            };

            _dropInsertionIndicator = new Border
            {
                Width = 36,
                Height = 24,
                Margin = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1.5),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xE0, 0x4D, 0x8C, 0xFF)),
                Background = new SolidColorBrush(Color.FromArgb(0x38, 0x4D, 0x8C, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };

            _wrapperPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _customItemsControl, _dropInsertionIndicator }
            };

            parentPanel.Children.Insert(Math.Min(parentPanel.Children.Count, statusIdx + 1), _wrapperPanel);
        }

        private static ItemsPanelTemplate CreateHorizontalItemsPanelTemplate()
        {
            var spFactory = new FrameworkElementFactory(typeof(StackPanel));
            spFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            spFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
            spFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            return new ItemsPanelTemplate(spFactory);
        }

        private DataTemplate CreatePinnedItemDataTemplate()
        {
            var dt = new DataTemplate(typeof(PinnedTrayItemViewModel));
            var btnFactory = new FrameworkElementFactory(typeof(Button));
            btnFactory.SetValue(FrameworkElement.WidthProperty, 36.0);
            btnFactory.SetValue(FrameworkElement.HeightProperty, 24.0);
            btnFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
            btnFactory.SetValue(Control.PaddingProperty, new Thickness(0));
            btnFactory.SetValue(Control.BackgroundProperty, Brushes.Transparent);
            btnFactory.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            btnFactory.SetValue(UIElement.FocusableProperty, false);
            btnFactory.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
            btnFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            btnFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            btnFactory.SetBinding(FrameworkElement.TagProperty, new Binding());
            btnFactory.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(PinnedTrayItemViewModel.DisplayName)));

            Style? honorBtnStyle = _topBarWindow.TryFindResource("TopBarIconButtonStyle") as Style;
            if (honorBtnStyle != null)
            {
                btnFactory.SetValue(FrameworkElement.StyleProperty, honorBtnStyle);
            }
            else
            {
                btnFactory.SetValue(Control.TemplateProperty, CreateFallbackIconButtonTemplate());
            }

            btnFactory.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPinnedButtonPreviewMouseLeftButtonDown), handledEventsToo: false);
            btnFactory.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(OnPinnedButtonPreviewMouseMove), handledEventsToo: false);
            btnFactory.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnPinnedButtonPreviewMouseLeftButtonUp), handledEventsToo: false);
            btnFactory.AddHandler(UIElement.PreviewMouseRightButtonUpEvent, new MouseButtonEventHandler(OnPinnedButtonPreviewMouseRightButtonUp), handledEventsToo: false);
            btnFactory.AddHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler(OnPinnedButtonLostMouseCapture), handledEventsToo: false);

            var hostGridFactory = new FrameworkElementFactory(typeof(Grid));
            hostGridFactory.SetValue(FrameworkElement.WidthProperty, 20.0);
            hostGridFactory.SetValue(FrameworkElement.HeightProperty, 20.0);
            hostGridFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            hostGridFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            var imgFactory = new FrameworkElementFactory(typeof(Image));
            imgFactory.SetValue(FrameworkElement.WidthProperty, 14.0);
            imgFactory.SetValue(FrameworkElement.HeightProperty, 14.0);
            imgFactory.SetValue(Image.StretchProperty, Stretch.Uniform);
            imgFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            imgFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            imgFactory.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            imgFactory.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
            imgFactory.SetBinding(Image.SourceProperty, new Binding(nameof(PinnedTrayItemViewModel.IconSource)));

            hostGridFactory.AppendChild(imgFactory);
            btnFactory.AppendChild(hostGridFactory);
            dt.VisualTree = btnFactory;
            return dt;
        }

        private static ControlTemplate CreateFallbackIconButtonTemplate()
        {
            var tmpl = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.Name = "PART_HoverBorder";
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            borderFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);

            var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            cpFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cpFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(cpFactory);

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)), "PART_HoverBorder"));

            tmpl.VisualTree = borderFactory;
            tmpl.Triggers.Add(hoverTrigger);
            return tmpl;
        }

        private void HookThemeObservers()
        {
            _themeChangedHandler = (_, _) => RefreshThemeOnUiThread();

            if (_dateTimeText != null)
            {
                _dateForegroundDescriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.ForegroundProperty, typeof(TextBlock));
                _dateForegroundDescriptor?.AddValueChanged(_dateTimeText, _themeChangedHandler);
            }

            if (_backgroundBorder != null)
            {
                _bgBorderDescriptor = DependencyPropertyDescriptor.FromProperty(Border.BackgroundProperty, typeof(Border));
                _bgBorderDescriptor?.AddValueChanged(_backgroundBorder, _themeChangedHandler);
            }

            RefreshThemeOnUiThread();
        }

        private void RefreshThemeOnUiThread()
        {
            if (_disposed)
            {
                return;
            }

            bool nextDark = DetectUseDarkForeground();
            if (nextDark == _useDarkForeground)
            {
                return;
            }

            _useDarkForeground = nextDark;
            foreach (PinnedTrayItemViewModel vm in _visibleItems)
            {
                vm.ApplyTheme(nextDark);
            }
        }

        private bool DetectUseDarkForeground()
        {
            if (_topBarWindow.TryFindResource("TopBarForegroundBrush") is SolidColorBrush fgBrush)
            {
                Color c = fgBrush.Color;
                double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
                return lum < 0.5;
            }

            if (_dateTimeText?.Foreground is SolidColorBrush textBrush)
            {
                Color c = textBrush.Color;
                double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
                return lum < 0.5;
            }

            return false;
        }

        private void ApplyOleDropSupport()
        {
            if (_disposed)
            {
                return;
            }

            _topBarWindow.RemoveHandler(DragDrop.PreviewDragOverEvent, _previewDragOverHandler);
            _topBarWindow.RemoveHandler(DragDrop.PreviewDropEvent, _previewDropHandler);
            _topBarWindow.RemoveHandler(DragDrop.PreviewDragLeaveEvent, _previewDragLeaveHandler);

            if (AdaptiveSettingsStore.IsHiddenTrayIconPinningEnabled)
            {
                _topBarWindow.AllowDrop = true;
                _topBarWindow.AddHandler(DragDrop.PreviewDragOverEvent, _previewDragOverHandler, handledEventsToo: true);
                _topBarWindow.AddHandler(DragDrop.PreviewDropEvent, _previewDropHandler, handledEventsToo: true);
                _topBarWindow.AddHandler(DragDrop.PreviewDragLeaveEvent, _previewDragLeaveHandler, handledEventsToo: true);
            }
            else
            {
                _topBarWindow.AllowDrop = _origAllowDrop;
            }
        }

        private void OnTopBarPreviewDragOver(object sender, DragEventArgs e)
        {
            if (_disposed || !AdaptiveSettingsStore.IsHiddenTrayIconPinningEnabled)
            {
                return;
            }

            if (OverflowDragCoordinator.HasActiveOverflowDrag)
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                SetDropHighlight(true);
            }
        }

        private void OnTopBarPreviewDragLeave(object sender, DragEventArgs e)
        {
            if (!_disposed && OverflowDragCoordinator.HasActiveOverflowDrag)
            {
                SetDropHighlight(false);
            }
        }

        private void OnTopBarPreviewDrop(object sender, DragEventArgs e)
        {
            if (_disposed || !AdaptiveSettingsStore.IsHiddenTrayIconPinningEnabled)
            {
                return;
            }

            if (OverflowDragCoordinator.HasActiveOverflowDrag)
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                SetDropHighlight(false);
                OverflowDragCoordinator.TryCommitActiveOleDrop(this);
            }
        }

        private void OnFeatureEnabledChanged(bool enabled)
        {
            if (_disposed || _topBarWindow.Dispatcher.HasShutdownStarted)
            {
                return;
            }

            _topBarWindow.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                if (_disposed)
                {
                    return;
                }

                ApplyOleDropSupport();
                UpdateVisibilityAndTimers();
                if (enabled)
                {
                    SyncVisibleItemsFromStoreSnapshot(forceIconRebuild: false);
                    QueueLiveRefresh();
                }
                else
                {
                    SetDropHighlight(false);
                }
            }));
        }

        private void OnStoreChanged()
        {
            if (_disposed || _topBarWindow.Dispatcher.HasShutdownStarted)
            {
                return;
            }

            _topBarWindow.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (!_disposed)
                {
                    SyncVisibleItemsFromStoreSnapshot(forceIconRebuild: false);
                    QueueLiveRefresh();
                }
            }));
        }

        private void UpdateVisibilityAndTimers()
        {
            bool enabled = AdaptiveSettingsStore.IsHiddenTrayIconPinningEnabled;
            if (_wrapperPanel != null)
            {
                _wrapperPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            }

            if (enabled && PinnedTrayStore.Count > 0)
            {
                if (!_liveRefreshTimer.IsEnabled)
                {
                    _liveRefreshTimer.Start();
                }
            }
            else if (_liveRefreshTimer.IsEnabled)
            {
                _liveRefreshTimer.Stop();
            }
        }

        private void SyncVisibleItemsFromStoreSnapshot(bool forceIconRebuild)
        {
            IReadOnlyList<PinnedTrayEntry> entries = PinnedTrayStore.GetSnapshot();
            if (!AdaptiveSettingsStore.IsHiddenTrayIconPinningEnabled || entries.Count == 0)
            {
                _visibleItems.Clear();
                UpdateVisibilityAndTimers();
                return;
            }

            var byId = new Dictionary<string, PinnedTrayItemViewModel>(StringComparer.Ordinal);
            foreach (PinnedTrayItemViewModel existing in _visibleItems)
            {
                byId[existing.PinId] = existing;
            }

            // Reorder or add active items; hidden/exited apps stay in PinnedTrayStore until they launch again.
            var desired = new List<PinnedTrayItemViewModel>(entries.Count);
            foreach (PinnedTrayEntry entry in entries)
            {
                if (byId.TryGetValue(entry.PinId, out PinnedTrayItemViewModel? vm))
                {
                    vm.UpdateFromEntry(entry, _useDarkForeground, forceIconRebuild);
                    if (vm.IsRunning)
                    {
                        desired.Add(vm);
                    }
                }
                else
                {
                    var created = new PinnedTrayItemViewModel(entry, _useDarkForeground);
                    // Newly pinned items start visible immediately while live refresh confirms ongoing state.
                    created.IsRunning = true;
                    desired.Add(created);
                }
            }

            ReconcileVisibleCollection(desired);
            UpdateVisibilityAndTimers();
        }

        private void ReconcileVisibleCollection(IReadOnlyList<PinnedTrayItemViewModel> desired)
        {
            for (int i = _visibleItems.Count - 1; i >= 0; i--)
            {
                if (!desired.Contains(_visibleItems[i]))
                {
                    _visibleItems.RemoveAt(i);
                }
            }

            for (int targetIdx = 0; targetIdx < desired.Count; targetIdx++)
            {
                PinnedTrayItemViewModel item = desired[targetIdx];
                int curIdx = _visibleItems.IndexOf(item);
                if (curIdx < 0)
                {
                    _visibleItems.Insert(targetIdx, item);
                }
                else if (curIdx != targetIdx)
                {
                    _visibleItems.Move(curIdx, targetIdx);
                }
            }
        }

        internal void QueueLiveRefresh()
        {
            if (_disposed || !AdaptiveSettingsStore.IsHiddenTrayIconPinningEnabled)
            {
                return;
            }

            if (PinnedTrayStore.Count == 0)
            {
                if (_visibleItems.Count > 0)
                {
                    _visibleItems.Clear();
                }
                UpdateVisibilityAndTimers();
                return;
            }

            if (Interlocked.Exchange(ref _refreshRunning, 1) == 1)
            {
                Interlocked.Exchange(ref _pendingRefresh, 1);
                return;
            }

            _ = RunLiveRefreshAsync();
        }

        private async Task RunLiveRefreshAsync()
        {
            try
            {
                do
                {
                    Interlocked.Exchange(ref _pendingRefresh, 0);
                    IReadOnlyList<PinnedTrayEntry> entries = PinnedTrayStore.GetSnapshot();
                    if (entries.Count == 0 || _disposed)
                    {
                        break;
                    }

                    LiveTraySnapshot snapshot = await Task.Run(() => NotifyIconCatalog.BuildLiveSnapshot(entries));
                    if (_disposed || _topBarWindow.Dispatcher.HasShutdownStarted)
                    {
                        break;
                    }

                    await _topBarWindow.Dispatcher.InvokeAsync(() =>
                    {
                        if (!_disposed)
                        {
                            ApplyLiveSnapshot(snapshot);
                        }
                    }, DispatcherPriority.Loaded);
                }
                while (Volatile.Read(ref _pendingRefresh) == 1 && !_disposed);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"HiddenTrayPinEnhancer live refresh error: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshRunning, 0);
            }
        }

        private void ApplyLiveSnapshot(LiveTraySnapshot snapshot)
        {
            IReadOnlyList<PinnedTrayEntry> entries = PinnedTrayStore.GetSnapshot();
            var existingById = new Dictionary<string, PinnedTrayItemViewModel>(StringComparer.Ordinal);
            foreach (PinnedTrayItemViewModel existing in _visibleItems)
            {
                existingById[existing.PinId] = existing;
            }

            var nextVisible = new List<PinnedTrayItemViewModel>(entries.Count);
            foreach (PinnedTrayEntry entry in entries)
            {
                snapshot.States.TryGetValue(entry.PinId, out LiveItemState? state);
                bool isRunning = state?.IsRunning ?? false;
                if (!isRunning)
                {
                    continue;
                }

                if (!existingById.TryGetValue(entry.PinId, out PinnedTrayItemViewModel? vm))
                {
                    vm = new PinnedTrayItemViewModel(entry, _useDarkForeground);
                }

                vm.IsRunning = true;
                if (state != null)
                {
                    vm.ApplyLiveState(state, _useDarkForeground);
                }
                nextVisible.Add(vm);
            }

            ReconcileVisibleCollection(nextVisible);
            UpdateVisibilityAndTimers();
        }

        private void OnPinnedButtonPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_disposed || sender is not Button btn || btn.Tag is not PinnedTrayItemViewModel vm)
            {
                return;
            }

            _suppressNextClick = false;
            if (e.ClickCount >= 2)
            {
                _barDragCandidate = null;
                _barDragButton = null;
                _barDragging = false;
                _suppressNextClick = true;
                e.Handled = true;
                TriggerPinnedItemClick(btn, vm, isRightClick: false, isDoubleClick: true);
                return;
            }

            if (GetCursorPos(out POINT pt))
            {
                _barDragCandidate = vm;
                _barDragButton = btn;
                _barDragStartPx = pt;
                _barDragging = false;
            }
        }

        private void OnPinnedButtonPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_disposed || _barDragCandidate == null || _barDragButton == null ||
                e.LeftButton != MouseButtonState.Pressed || !GetCursorPos(out POINT pt))
            {
                return;
            }

            UpdateTopBarDragVisuals(
                _barDragButton,
                _barDragCandidate.IconSource,
                _barDragCandidate.DisplayName,
                _barDragStartPx,
                pt,
                ref _barDragging,
                ref _suppressNextClick);
        }

        private void OnPinnedButtonPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_disposed || sender is not Button btn || btn.Tag is not PinnedTrayItemViewModel vm)
            {
                return;
            }

            bool wasDragging = _barDragging;
            PinnedTrayItemViewModel? dragged = _barDragCandidate;
            _barDragCandidate = null;
            _barDragButton = null;
            _barDragging = false;

            if (btn.IsMouseCaptured)
            {
                btn.ReleaseMouseCapture();
            }

            SetTrayArrowDropHighlight(false);
            OverflowDragCoordinator.HideBarReorderGhost();

            if (wasDragging && dragged != null)
            {
                e.Handled = true;
                if (GetCursorPos(out POINT pt))
                {
                    IntPtr topHwnd = new WindowInteropHelper(_topBarWindow).Handle;
                    double scale = GetDpiScaleForWindow(topHwnd);
                    if (IsOverUnpinTarget(topHwnd, pt, scale, out bool overArrow))
                    {
                        PinnedTrayStore.Remove(dragged.PinId);
                        DiagnosticLog.Info($"Unpinned custom tray icon (overArrow={overArrow}): pinId={dragged.PinId}.");
                        return;
                    }

                    TryReorderByScreenPoint(dragged.PinId, pt);
                }
                return;
            }

            if (_suppressNextClick)
            {
                _suppressNextClick = false;
                e.Handled = true;
                return;
            }

            e.Handled = true;
            TriggerPinnedItemClick(btn, vm, isRightClick: false, isDoubleClick: false);
        }

        private void OnPinnedButtonLostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_barDragging)
            {
                _barDragging = false;
                _barDragCandidate = null;
                _barDragButton = null;
                SetTrayArrowDropHighlight(false);
                OverflowDragCoordinator.HideBarReorderGhost();
            }
        }

        private void AttachNativeStatusDragHandlers()
        {
            if (_statusItemsControl == null)
            {
                return;
            }

            _statusItemsControl.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, _nativeDownHandler, handledEventsToo: false);
            _statusItemsControl.AddHandler(UIElement.PreviewMouseMoveEvent, _nativeMoveHandler, handledEventsToo: false);
            _statusItemsControl.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, _nativeUpHandler, handledEventsToo: false);
            _statusItemsControl.AddHandler(UIElement.LostMouseCaptureEvent, _nativeLostCaptureHandler, handledEventsToo: false);
        }

        private void DetachNativeStatusDragHandlers()
        {
            if (_statusItemsControl == null)
            {
                return;
            }

            _statusItemsControl.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, _nativeDownHandler);
            _statusItemsControl.RemoveHandler(UIElement.PreviewMouseMoveEvent, _nativeMoveHandler);
            _statusItemsControl.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, _nativeUpHandler);
            _statusItemsControl.RemoveHandler(UIElement.LostMouseCaptureEvent, _nativeLostCaptureHandler);
        }

        private void OnNativeStatusPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_disposed || !AdaptiveSettingsStore.IsHiddenTrayIconPinningEnabled || e.ClickCount != 1)
            {
                return;
            }

            Button? btn = StatusNotificationTrayRelay.FindNotificationButton(e.OriginalSource as DependencyObject);
            object? item = btn?.Tag ?? btn?.DataContext;
            if (btn == null || item == null)
            {
                return;
            }

            var (appId, displayName, launchPath, procNames) = StatusNotificationTrayRelay.ReadNotificationItemMetadata(item);
            if (string.IsNullOrWhiteSpace(appId) || !GetCursorPos(out POINT pt))
            {
                return;
            }

            string? fallbackProc = procNames != null && procNames.Count > 0 ? StripExe(procNames[0]) : appId;
            HashSet<uint> targetPids = FindMatchingProcessIds(launchPath, fallbackProc);
            var (prevVisible, prevRestored) = SnapshotVisibleAppWindows(targetPids);

            _nativeSuppressNextClick = false;
            _nativeDragAppId = appId;
            _nativeDragDisplayName = string.IsNullOrWhiteSpace(displayName) ? appId : displayName;
            _nativeDragLaunchPath = launchPath;
            _nativeDragProcNames = procNames;
            _nativeDragIcon = FindButtonImageSource(btn);
            _nativeDragButton = btn;
            _nativeDragStartPx = pt;
            _nativeBarDragging = false;
            _nativeClickPids = targetPids;
            _nativeClickPrevFg = GetForegroundWindow();
            _nativeClickPrevVisible = prevVisible;
            _nativeClickPrevRestored = prevRestored;
        }

        private void OnNativeStatusPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_disposed || _nativeDragButton == null || string.IsNullOrEmpty(_nativeDragAppId) ||
                e.LeftButton != MouseButtonState.Pressed || !GetCursorPos(out POINT pt))
            {
                return;
            }

            UpdateTopBarDragVisuals(
                _nativeDragButton,
                _nativeDragIcon,
                _nativeDragDisplayName ?? _nativeDragAppId,
                _nativeDragStartPx,
                pt,
                ref _nativeBarDragging,
                ref _nativeSuppressNextClick);
        }

        private void OnNativeStatusPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            bool wasDragging = _nativeBarDragging;
            string? appId = _nativeDragAppId;
            string? displayName = _nativeDragDisplayName;
            string? launchPath = _nativeDragLaunchPath;
            IReadOnlyList<string>? procNames = _nativeDragProcNames;
            HashSet<uint>? targetPids = _nativeClickPids;
            IntPtr prevFg = _nativeClickPrevFg;
            HashSet<IntPtr>? prevVisible = _nativeClickPrevVisible;
            HashSet<IntPtr>? prevRestored = _nativeClickPrevRestored;
            Button? btn = _nativeDragButton;

            _nativeDragAppId = null;
            _nativeDragDisplayName = null;
            _nativeDragLaunchPath = null;
            _nativeDragProcNames = null;
            _nativeDragIcon = null;
            _nativeDragButton = null;
            _nativeBarDragging = false;
            _nativeClickPids = null;
            _nativeClickPrevVisible = null;
            _nativeClickPrevRestored = null;

            if (btn != null && btn.IsMouseCaptured)
            {
                btn.ReleaseMouseCapture();
            }

            SetTrayArrowDropHighlight(false);
            OverflowDragCoordinator.HideBarReorderGhost();

            if (wasDragging && !string.IsNullOrWhiteSpace(appId))
            {
                e.Handled = true;
                if (GetCursorPos(out POINT pt))
                {
                    IntPtr topHwnd = new WindowInteropHelper(_topBarWindow).Handle;
                    double scale = GetDpiScaleForWindow(topHwnd);
                    if (IsOverUnpinTarget(topHwnd, pt, scale, out bool overArrow))
                    {
                        try
                        {
                            _disableStatusAppMethod?.Invoke(_topBarWindow, new object[] { appId });
                            DiagnosticLog.Info($"Unpinned native status icon (overArrow={overArrow}): appId={appId}.");
                        }
                        catch (Exception ex)
                        {
                            DiagnosticLog.Warn($"DisableStatusNotificationApp failed for '{appId}': {ex.Message}");
                        }
                    }
                }
                return;
            }

            if (_nativeSuppressNextClick)
            {
                _nativeSuppressNextClick = false;
                e.Handled = true;
                return;
            }

            if (!string.IsNullOrWhiteSpace(appId) && targetPids != null && prevVisible != null && prevRestored != null)
            {
                long clickGen = Interlocked.Increment(ref _clickSequenceGen);
                _ = GetCursorPos(out POINT curPt);
                string? procName = procNames != null && procNames.Count > 0 ? StripExe(procNames[0]) : appId;
                var req = new CustomTrayClickRequest(
                    launchPath,
                    procName,
                    displayName ?? appId,
                    null,
                    null,
                    IntPtr.Zero,
                    null,
                    null,
                    curPt.X,
                    curPt.Y,
                    IsRightClick: false,
                    IsDoubleClick: false);
                _ = EnsureNativeStatusSingleClickActivatedAsync(req, clickGen, targetPids, prevFg, prevVisible, prevRestored);
            }
        }

        internal static async Task ActivateAppViaTrayOrLaunchAsync(string? executablePath, string? processName, string? displayName)
        {
            try
            {
                HashSet<uint> targetPids = FindMatchingProcessIds(executablePath, processName);
                IntPtr prevFg = GetForegroundWindow();
                (HashSet<IntPtr> prevVisible, HashSet<IntPtr> prevRestored) = SnapshotVisibleAppWindows(targetPids);

                var singleReq = new CustomTrayClickRequest(
                    executablePath,
                    processName,
                    displayName,
                    null,
                    null,
                    IntPtr.Zero,
                    null,
                    null,
                    0,
                    0,
                    IsRightClick: false,
                    IsDoubleClick: false);

                bool relayed = await StatusNotificationTrayRelay.TryRelayCustomTrayRequestAsync(singleReq).ConfigureAwait(false);
                if (relayed)
                {
                    await Task.Delay(95).ConfigureAwait(false);
                    if (DidClickActivateTarget(targetPids, prevFg, prevVisible, prevRestored))
                    {
                        return;
                    }

                    var dblReq = singleReq with { IsDoubleClick = true };
                    _ = await StatusNotificationTrayRelay.TryRelayCustomTrayRequestAsync(dblReq).ConfigureAwait(false);
                    DiagnosticLog.Info($"Auto-escalated bridged card activation to tray double-click (app='{processName ?? displayName}').");

                    await Task.Delay(110).ConfigureAwait(false);
                    if (DidClickActivateTarget(targetPids, prevFg, prevVisible, prevRestored))
                    {
                        return;
                    }
                }

                await Task.Run(() => ActivateOrLaunchFallback(executablePath, processName)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"ActivateAppViaTrayOrLaunchAsync failed: {ex.Message}");
            }
        }

        private async Task EnsureNativeStatusSingleClickActivatedAsync(
            CustomTrayClickRequest req,
            long clickGen,
            HashSet<uint> targetPids,
            IntPtr prevFg,
            HashSet<IntPtr> prevVisible,
            HashSet<IntPtr> prevRestored)
        {
            try
            {
                await Task.Delay(150).ConfigureAwait(false);
                if (_disposed || Volatile.Read(ref _clickSequenceGen) != clickGen)
                {
                    return;
                }

                if (DidClickActivateTarget(targetPids, prevFg, prevVisible, prevRestored))
                {
                    return;
                }

                bool relayed = await RelayTrayClickCoreAsync(req).ConfigureAwait(false);
                if (relayed)
                {
                    await EscalateSingleClickIfNeededAsync(req, clickGen, targetPids, prevFg, prevVisible, prevRestored).ConfigureAwait(false);
                }
                else
                {
                    await Task.Run(() => ActivateOrLaunchFallback(req.ExecutablePath, req.ProcessName)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"EnsureNativeStatusSingleClickActivatedAsync error: {ex.Message}");
            }
        }

        private void OnNativeStatusLostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_nativeBarDragging)
            {
                _nativeBarDragging = false;
                _nativeDragAppId = null;
                _nativeDragDisplayName = null;
                _nativeDragIcon = null;
                _nativeDragButton = null;
                SetTrayArrowDropHighlight(false);
                OverflowDragCoordinator.HideBarReorderGhost();
            }
        }

        private static ImageSource? FindButtonImageSource(DependencyObject root)
        {
            if (root is Image img && img.Source != null)
            {
                return img.Source;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                ImageSource? found = FindButtonImageSource(VisualTreeHelper.GetChild(root, i));
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        private void UpdateTopBarDragVisuals(
            Button btn,
            ImageSource? icon,
            string label,
            POINT startPx,
            POINT curPx,
            ref bool dragging,
            ref bool suppressClick)
        {
            IntPtr topHwnd = new WindowInteropHelper(_topBarWindow).Handle;
            double scale = GetDpiScaleForWindow(topHwnd);
            int threshPx = Math.Max(10, (int)Math.Round(10.0 * scale));
            int dx = curPx.X - startPx.X;
            int dy = curPx.Y - startPx.Y;

            if (!dragging)
            {
                if (dx * dx + dy * dy < threshPx * threshPx)
                {
                    return;
                }

                dragging = true;
                suppressClick = true;
                btn.CaptureMouse();
            }

            bool unpinIntent = IsOverUnpinTarget(topHwnd, curPx, scale, out bool overArrow);
            SetTrayArrowDropHighlight(overArrow);
            string ghostLabel = overArrow
                ? $"松开收进托盘 · {label}"
                : (unpinIntent ? $"松开取消固定 · {label}" : label);
            OverflowDragCoordinator.UpdateBarReorderGhost(icon, ghostLabel, unpinIntent, curPx);
        }

        internal bool IsPointOverTrayArrowPx(POINT pt, double scale)
        {
            if (_trayButton == null || !_trayButton.IsVisible ||
                _trayButton.ActualWidth <= 0 || _trayButton.ActualHeight <= 0)
            {
                return false;
            }

            try
            {
                Point tl = _trayButton.PointToScreen(new Point(0, 0));
                Point br = _trayButton.PointToScreen(new Point(_trayButton.ActualWidth, _trayButton.ActualHeight));
                int padX = Math.Max(8, (int)Math.Round(10.0 * scale));
                int padY = Math.Max(6, (int)Math.Round(8.0 * scale));
                return pt.X >= tl.X - padX && pt.X <= br.X + padX &&
                       pt.Y >= tl.Y - padY && pt.Y <= br.Y + padY;
            }
            catch
            {
                return false;
            }
        }

        internal bool IsOverUnpinTarget(IntPtr topBarHwnd, POINT pt, double scale, out bool overArrow)
        {
            overArrow = IsPointOverTrayArrowPx(pt, scale);
            return overArrow || IsOutsideUnpinThreshold(topBarHwnd, pt, scale);
        }

        private void SetTrayArrowDropHighlight(bool active)
        {
            if (_trayButton == null || _trayArrowHighlighted == active)
            {
                return;
            }

            if (active)
            {
                _trayArrowOrigBackground = _trayButton.Background;
                _trayButton.Background = new SolidColorBrush(Color.FromArgb(0x55, 0x38, 0x8B, 0xFF));
                _trayArrowHighlighted = true;
            }
            else
            {
                _trayButton.Background = _trayArrowOrigBackground;
                _trayArrowOrigBackground = null;
                _trayArrowHighlighted = false;
            }
        }

        private void OnPinnedButtonPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_disposed || sender is not Button btn || btn.Tag is not PinnedTrayItemViewModel vm)
            {
                return;
            }

            e.Handled = true;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ||
                !AdaptiveSettingsStore.IsNativeTrayRightClickEnabled)
            {
                ShowUnpinMenu(btn, vm, e.GetPosition(_topBarWindow));
                return;
            }

            TriggerPinnedItemClick(btn, vm, isRightClick: true, isDoubleClick: false, fallbackPosDip: e.GetPosition(_topBarWindow));
        }

        private bool IsOutsideUnpinThreshold(IntPtr topBarHwnd, POINT pt, double scale)
        {
            if (topBarHwnd == IntPtr.Zero || !GetWindowRect(topBarHwnd, out RECT r))
            {
                return false;
            }

            int marginPx = Math.Max(16, (int)Math.Round(24.0 * scale));
            return pt.Y > r.Bottom + marginPx;
        }

        private void TryReorderByScreenPoint(string draggedPinId, POINT pt)
        {
            if (_customItemsControl == null || _visibleItems.Count < 2)
            {
                return;
            }

            for (int i = 0; i < _customItemsControl.Items.Count; i++)
            {
                if (_customItemsControl.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                {
                    continue;
                }

                if (!container.IsVisible || container.ActualWidth <= 0)
                {
                    continue;
                }

                Point tl = container.PointToScreen(new Point(0, 0));
                Point br = container.PointToScreen(new Point(container.ActualWidth, container.ActualHeight));
                if (pt.X >= tl.X && pt.X <= br.X && _visibleItems[i].PinId != draggedPinId)
                {
                    PinnedTrayStore.MoveBeforeOrAfter(draggedPinId, _visibleItems[i].PinId);
                    return;
                }
            }
        }

        private void TriggerPinnedItemClick(
            Button btn,
            PinnedTrayItemViewModel vm,
            bool isRightClick,
            bool isDoubleClick,
            Point? fallbackPosDip = null)
        {
            int clickX;
            int clickY;
            if (GetCursorPos(out POINT cur))
            {
                clickX = cur.X;
                clickY = cur.Y;
            }
            else
            {
                Point p = btn.PointToScreen(new Point(btn.ActualWidth / 2.0, Math.Max(1.0, btn.ActualHeight)));
                clickX = (int)Math.Round(p.X);
                clickY = (int)Math.Round(p.Y);
            }

            Point posDip = fallbackPosDip ?? btn.TranslatePoint(new Point(btn.ActualWidth / 2.0, btn.ActualHeight), _topBarWindow);
            _ = ExecutePinnedItemClickAsync(btn, vm, clickX, clickY, isRightClick, isDoubleClick, posDip);
        }

        private async Task ExecutePinnedItemClickAsync(
            Button btn,
            PinnedTrayItemViewModel vm,
            int clickX,
            int clickY,
            bool isRightClick,
            bool isDoubleClick,
            Point posDip)
        {
            long clickGen = Interlocked.Increment(ref _clickSequenceGen);
            try
            {
                CloseTopBarPopups();
                await Task.Yield();
                if (_disposed)
                {
                    return;
                }

                var req = new CustomTrayClickRequest(
                    vm.Entry.ExecutablePath,
                    vm.Entry.ProcessName,
                    vm.DisplayName,
                    vm.LiveUid ?? vm.Entry.NotifyIconUid,
                    Guid.TryParse(vm.Entry.IconGuid, out Guid parsedGuid) ? parsedGuid : null,
                    vm.LiveTrayHwnd,
                    vm.LiveCallbackMessage,
                    vm.LiveIsV4,
                    clickX,
                    clickY,
                    isRightClick,
                    isDoubleClick);

                HashSet<uint>? targetPids = null;
                IntPtr prevFg = IntPtr.Zero;
                HashSet<IntPtr>? prevVisible = null;
                HashSet<IntPtr>? prevRestored = null;
                if (!isRightClick && !isDoubleClick)
                {
                    targetPids = FindMatchingProcessIds(vm.Entry.ExecutablePath, vm.Entry.ProcessName);
                    prevFg = GetForegroundWindow();
                    (prevVisible, prevRestored) = SnapshotVisibleAppWindows(targetPids);
                }

                bool relayed = await RelayTrayClickCoreAsync(req);

                if (_disposed)
                {
                    return;
                }

                if (relayed && !isRightClick && !isDoubleClick &&
                    targetPids != null && prevVisible != null && prevRestored != null)
                {
                    _ = EscalateSingleClickIfNeededAsync(req, clickGen, targetPids, prevFg, prevVisible, prevRestored);
                }
                else if (!relayed)
                {
                    if (isRightClick)
                    {
                        ShowUnpinMenu(btn, vm, posDip);
                    }
                    else
                    {
                        _ = Task.Run(() => ActivateOrLaunchFallback(vm.Entry.ExecutablePath, vm.Entry.ProcessName));
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"Pinned tray click failed: {ex.Message}");
            }
        }

        private async Task<bool> RelayTrayClickCoreAsync(CustomTrayClickRequest req)
        {
            CustomTrayClickHandler? externalRelay = Volatile.Read(ref _externalTrayRelay);
            bool relayed = false;
            if (externalRelay != null)
            {
                try
                {
                    relayed = await externalRelay(req);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warn($"External custom tray relay error: {ex.Message}");
                }
            }

            if (!relayed)
            {
                relayed = await Task.Run(() => FallbackRelayTrayClick(req));
            }

            return relayed;
        }

        private async Task EscalateSingleClickIfNeededAsync(
            CustomTrayClickRequest singleReq,
            long clickGen,
            HashSet<uint> targetPids,
            IntPtr prevFg,
            HashSet<IntPtr> prevVisible,
            HashSet<IntPtr> prevRestored)
        {
            try
            {
                await Task.Delay(95).ConfigureAwait(false);
                if (_disposed || Volatile.Read(ref _clickSequenceGen) != clickGen)
                {
                    return;
                }

                if (DidClickActivateTarget(targetPids, prevFg, prevVisible, prevRestored))
                {
                    return;
                }

                var dblReq = singleReq with { IsDoubleClick = true };

                _ = await RelayTrayClickCoreAsync(dblReq).ConfigureAwait(false);
                DiagnosticLog.Info($"Auto-escalated tray single-click to double-click (app='{singleReq.ProcessName ?? singleReq.DisplayName}').");

                await Task.Delay(110).ConfigureAwait(false);
                if (_disposed || Volatile.Read(ref _clickSequenceGen) != clickGen)
                {
                    return;
                }

                if (!DidClickActivateTarget(targetPids, prevFg, prevVisible, prevRestored))
                {
                    await Task.Run(() => ActivateOrLaunchFallback(singleReq.ExecutablePath, singleReq.ProcessName)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"EscalateSingleClickIfNeededAsync error: {ex.Message}");
            }
        }

        internal static bool DidClickActivateTarget(
            HashSet<uint> targetPids,
            IntPtr prevFg,
            HashSet<IntPtr> prevVisible,
            HashSet<IntPtr> prevRestored)
        {
            var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(0, ref gti) &&
                ((gti.flags & (GuiInMenuMode | GuiPopupMenuMode)) != 0 || gti.hwndMenuOwner != IntPtr.Zero))
            {
                return true;
            }

            if (targetPids.Count == 0)
            {
                return false;
            }

            IntPtr currFg = GetForegroundWindow();
            if (currFg != IntPtr.Zero && currFg != prevFg)
            {
                _ = GetWindowThreadProcessId(currFg, out uint fgPid);
                if (targetPids.Contains(fgPid))
                {
                    return true;
                }
            }

            var (currVisible, currRestored) = SnapshotVisibleAppWindows(targetPids);
            foreach (IntPtr h in currVisible)
            {
                if (!prevVisible.Contains(h))
                {
                    return true;
                }
            }

            foreach (IntPtr h in currRestored)
            {
                if (!prevRestored.Contains(h))
                {
                    return true;
                }
            }

            return false;
        }

        private static (HashSet<IntPtr> Visible, HashSet<IntPtr> Restored) SnapshotVisibleAppWindows(HashSet<uint> targetPids)
        {
            var visible = new HashSet<IntPtr>();
            var restored = new HashSet<IntPtr>();
            if (targetPids.Count == 0)
            {
                return (visible, restored);
            }

            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h))
                {
                    return true;
                }

                GetWindowThreadProcessId(h, out uint pid);
                if (!targetPids.Contains(pid))
                {
                    return true;
                }

                string cls = GetWindowClassNameSafe(h);
                if (StatusNotificationTrayRelay.IsSystemHelperWindowClass(cls) ||
                    cls.Contains("Tray", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("NotifyIcon", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (GetWindowRect(h, out RECT r) && (r.Right - r.Left) > 12 && (r.Bottom - r.Top) > 12)
                {
                    visible.Add(h);
                    if (!IsIconic(h))
                    {
                        restored.Add(h);
                    }
                }
                return true;
            }, IntPtr.Zero);

            return (visible, restored);
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
                }
            }
        }

        private void ShowUnpinMenu(Button btn, PinnedTrayItemViewModel vm, Point posInTopBarDip)
        {
            if (_disposed || _topBarWindow.Dispatcher.HasShutdownStarted)
            {
                return;
            }

            Action removeAction = () =>
            {
                PinnedTrayStore.Remove(vm.PinId);
                DiagnosticLog.Info($"Removed custom pinned tray icon via menu: pinId={vm.PinId}.");
            };

            if (_openRemoveMenuMethod != null)
            {
                try
                {
                    _openRemoveMenuMethod.Invoke(_topBarWindow, new object[] { btn, "从顶部栏移除", removeAction, posInTopBarDip });
                    return;
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warn($"OpenBarItemRemoveContextMenu failed, using fallback ContextMenu: {ex.Message}");
                }
            }

            var cm = new ContextMenu();
            var mi = new MenuItem { Header = "从顶部栏移除" };
            mi.Click += (_, _) => removeAction();
            cm.Items.Add(mi);
            cm.PlacementTarget = btn;
            cm.Placement = PlacementMode.Bottom;
            cm.IsOpen = true;
        }

        internal static bool FallbackRelayTrayClick(CustomTrayClickRequest req)
        {
            var resolved = ResolveLiveTrayWindow(req);
            if (resolved.Hwnd == IntPtr.Zero || !IsWindow(resolved.Hwnd))
            {
                return false;
            }

            if (resolved.Pid != 0)
            {
                _ = AllowSetForegroundWindow(resolved.Pid);
            }

            uint uid = resolved.Uid;
            uint msg = resolved.CallbackMessage;
            bool isV4 = resolved.IsV4;

            bool posted = req.IsRightClick
                ? PostTraySequence(resolved.Hwnd, msg, uid, req.ScreenX, req.ScreenY, isV4,
                    WmMouseMove, WmRButtonDown, WmRButtonUp, includeContextMenu: isV4)
                : (req.IsDoubleClick
                    ? PostTraySequence(resolved.Hwnd, msg, uid, req.ScreenX, req.ScreenY, isV4,
                        WmMouseMove, WmLButtonDblClk, WmLButtonUp, includeContextMenu: false)
                    : PostTraySequence(resolved.Hwnd, msg, uid, req.ScreenX, req.ScreenY, isV4,
                        WmMouseMove, WmLButtonDown, WmLButtonUp, includeNinSelect: isV4));

            DiagnosticLog.Info(
                $"Relayed fallback custom tray click (app={req.ExecutablePath ?? req.ProcessName ?? req.DisplayName}) " +
                $"hwnd=0x{resolved.Hwnd.ToInt64():X} pid={resolved.Pid} uid={uid} msg=0x{msg:X} v4={isV4} right={req.IsRightClick} posted={posted}.");
            return posted;
        }

        private static bool PostTraySequence(
            IntPtr hwnd,
            uint callbackMsg,
            uint uid,
            int cursorX,
            int cursorY,
            bool isV4,
            uint m1,
            uint m2,
            uint m3,
            bool includeContextMenu = false,
            bool includeNinSelect = false)
        {
            if (isV4)
            {
                uint xy = (((uint)cursorY & 0xFFFFu) << 16) | ((uint)cursorX & 0xFFFFu);
                var wParam = new UIntPtr(xy);
                uint hiUid = uid << 16;

                bool ok = PostMessage(hwnd, callbackMsg, wParam, new IntPtr(unchecked((int)(hiUid | m1))));
                ok |= PostMessage(hwnd, callbackMsg, wParam, new IntPtr(unchecked((int)(hiUid | m2))));
                ok |= PostMessage(hwnd, callbackMsg, wParam, new IntPtr(unchecked((int)(hiUid | m3))));
                if (includeContextMenu)
                {
                    ok |= PostMessage(hwnd, callbackMsg, wParam, new IntPtr(unchecked((int)(hiUid | WmContextMenu))));
                }
                if (includeNinSelect)
                {
                    ok |= PostMessage(hwnd, callbackMsg, wParam, new IntPtr(unchecked((int)(hiUid | NinSelect))));
                }
                return ok;
            }
            else
            {
                var wParam = new UIntPtr(uid);
                bool ok = PostMessage(hwnd, callbackMsg, wParam, new IntPtr((int)m1));
                ok |= PostMessage(hwnd, callbackMsg, wParam, new IntPtr((int)m2));
                ok |= PostMessage(hwnd, callbackMsg, wParam, new IntPtr((int)m3));
                return ok;
            }
        }

        private static (IntPtr Hwnd, uint Pid, uint CallbackMessage, uint Uid, bool IsV4) ResolveLiveTrayWindow(CustomTrayClickRequest req)
        {
            if (req.KnownHwnd != IntPtr.Zero && IsWindow(req.KnownHwnd) && req.KnownCallbackMessage.HasValue)
            {
                _ = GetWindowThreadProcessId(req.KnownHwnd, out uint knownPid);
                return (req.KnownHwnd, knownPid, req.KnownCallbackMessage.Value, req.KnownUid ?? 0u, req.KnownIsNotifyIconV4 ?? false);
            }

            var targetPids = new HashSet<uint>();
            string? normExe = string.IsNullOrWhiteSpace(req.ExecutablePath) ? null : NormalizePath(req.ExecutablePath);
            string? procBase = !string.IsNullOrWhiteSpace(req.ProcessName)
                ? StripExe(req.ProcessName)
                : (!string.IsNullOrWhiteSpace(normExe) ? Path.GetFileNameWithoutExtension(normExe) : null);

            foreach (Process p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        string pName = p.ProcessName;
                        bool nameMatch = !string.IsNullOrWhiteSpace(procBase) &&
                                         string.Equals(pName, procBase, StringComparison.OrdinalIgnoreCase);
                        string? pPath = NotifyIconCatalog.TryGetProcessPathSafe(p);
                        bool pathMatch = !string.IsNullOrWhiteSpace(normExe) && !string.IsNullOrWhiteSpace(pPath) &&
                                         string.Equals(NormalizePath(pPath), normExe, StringComparison.OrdinalIgnoreCase);
                        if (pathMatch || nameMatch)
                        {
                            targetPids.Add((uint)p.Id);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            if (targetPids.Count == 0)
            {
                return (IntPtr.Zero, 0, 0, 0, false);
            }

            IntPtr bestHwnd = IntPtr.Zero;
            uint bestPid = 0;
            uint bestMsg = 0x8001;
            uint bestUid = req.KnownUid ?? 0u;
            bool bestV4 = false;
            int bestScore = int.MinValue;

            void InspectWindow(IntPtr h)
            {
                _ = GetWindowThreadProcessId(h, out uint pid);
                if (!targetPids.Contains(pid))
                {
                    return;
                }

                string cls = GetWindowClassNameSafe(h);
                if (string.IsNullOrEmpty(cls) || StatusNotificationTrayRelay.IsSystemHelperWindowClass(cls))
                {
                    return;
                }

                int score = 0;
                uint uid = req.KnownUid ?? 0u;

                if (string.Equals(cls, "Electron_NotifyIconHostWindow", StringComparison.Ordinal))
                {
                    score = 100;
                    uid = req.KnownUid ?? 3u;
                }
                else if (cls.StartsWith("QTrayIconMessageWindow", StringComparison.Ordinal) ||
                         cls.StartsWith("EVERYTHING_TASKBAR_NOTIFICATION", StringComparison.OrdinalIgnoreCase))
                {
                    score = 95;
                }
                else if (cls.Contains("Tray", StringComparison.OrdinalIgnoreCase) ||
                         cls.Contains("NotifyIcon", StringComparison.OrdinalIgnoreCase))
                {
                    score = 80;
                }
                else if (cls.StartsWith("WindowsForms10.", StringComparison.Ordinal) ||
                         cls.Contains(".NET-BroadcastEventWindow", StringComparison.Ordinal))
                {
                    score = 65;
                }
                else if (cls.Contains("QWindowIcon", StringComparison.Ordinal))
                {
                    score = 60;
                }
                else if (cls.Contains("Notification", StringComparison.OrdinalIgnoreCase) ||
                         cls.Contains("Taskbar", StringComparison.OrdinalIgnoreCase))
                {
                    score = 45;
                }
                else if (!IsWindowVisible(h))
                {
                    score = 35;
                }
                else
                {
                    return;
                }

                var (msg, v4) = StatusNotificationTrayRelay.InferCallbackProtocol(
                    h,
                    pid,
                    cls,
                    req.ExecutablePath,
                    req.KnownCallbackMessage ?? 0u,
                    req.KnownIsNotifyIconV4);
                if (msg == 0)
                {
                    return;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestHwnd = h;
                    bestPid = pid;
                    bestMsg = msg;
                    bestUid = uid;
                    bestV4 = v4;
                }
            }

            EnumWindows((h, _) =>
            {
                InspectWindow(h);
                return bestScore < 100;
            }, IntPtr.Zero);

            if (bestScore < 100)
            {
                IntPtr child = IntPtr.Zero;
                int guard = 0;
                while (guard++ < 2000)
                {
                    child = FindWindowEx(HwndMessage, child, null, null);
                    if (child == IntPtr.Zero)
                    {
                        break;
                    }
                    InspectWindow(child);
                }
            }

            return (bestHwnd, bestPid, bestMsg, bestUid, bestV4);
        }

        private static HashSet<uint> FindMatchingProcessIds(string? exePath, string? procName)
        {
            var pids = new HashSet<uint>();
            string? normExe = string.IsNullOrWhiteSpace(exePath) ? null : NormalizePath(exePath);
            string? procBase = !string.IsNullOrWhiteSpace(procName)
                ? StripExe(procName)
                : (!string.IsNullOrWhiteSpace(normExe) ? Path.GetFileNameWithoutExtension(normExe) : null);

            foreach (Process p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        string? pPath = NotifyIconCatalog.TryGetProcessPathSafe(p);
                        bool match = (!string.IsNullOrWhiteSpace(normExe) && !string.IsNullOrWhiteSpace(pPath) &&
                                      string.Equals(NormalizePath(pPath), normExe, StringComparison.OrdinalIgnoreCase)) ||
                                     (!string.IsNullOrWhiteSpace(procBase) &&
                                      string.Equals(p.ProcessName, procBase, StringComparison.OrdinalIgnoreCase));
                        if (match)
                        {
                            pids.Add((uint)p.Id);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            return pids;
        }

        private static void ActivateOrLaunchFallback(string? exePath, string? procName)
        {
            try
            {
                HashSet<uint> pids = FindMatchingProcessIds(exePath, procName);

                if (pids.Count > 0)
                {
                    IntPtr visibleTop = IntPtr.Zero;
                    IntPtr hiddenTop = IntPtr.Zero;
                    EnumWindows((h, lParam) =>
                    {
                        _ = lParam;
                        _ = GetWindowThreadProcessId(h, out uint pid);
                        if (!pids.Contains(pid))
                        {
                            return true;
                        }

                        string cls = GetWindowClassNameSafe(h);
                        if (cls.Contains("Tray", StringComparison.OrdinalIgnoreCase) ||
                            cls.Equals("Default IME", StringComparison.OrdinalIgnoreCase) ||
                            cls.Equals("MSCTFIME UI", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }

                        if (IsWindowVisible(h))
                        {
                            visibleTop = h;
                            return false;
                        }

                        if (hiddenTop == IntPtr.Zero && GetWindowTextLength(h) > 0)
                        {
                            hiddenTop = h;
                        }

                        return true;
                    }, IntPtr.Zero);

                    IntPtr target = visibleTop != IntPtr.Zero ? visibleTop : hiddenTop;
                    if (target != IntPtr.Zero)
                    {
                        _ = GetWindowThreadProcessId(target, out uint tpid);
                        _ = AllowSetForegroundWindow(tpid);
                        ShowWindow(target, IsIconic(target) ? SwRestore : SwShow);
                        SetForegroundWindow(target);
                        return;
                    }
                }

                if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"ActivateOrLaunchFallback failed: {ex.Message}");
            }
        }

        private void OnTopBarWindowClosed(object? sender, EventArgs e)
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
            _liveRefreshTimer.Stop();
            OverflowDragCoordinator.UnregisterEnhancer(this);
            PinnedTrayStore.StoreChanged -= OnStoreChanged;
            AdaptiveSettingsStore.HiddenTrayIconPinningChanged -= OnFeatureEnabledChanged;
            _topBarWindow.Closed -= OnTopBarWindowClosed;

            _topBarWindow.RemoveHandler(DragDrop.PreviewDragOverEvent, _previewDragOverHandler);
            _topBarWindow.RemoveHandler(DragDrop.PreviewDropEvent, _previewDropHandler);
            _topBarWindow.RemoveHandler(DragDrop.PreviewDragLeaveEvent, _previewDragLeaveHandler);
            DetachNativeStatusDragHandlers();
            SetTrayArrowDropHighlight(false);
            _topBarWindow.AllowDrop = _origAllowDrop;

            if (_dateTimeText != null && _themeChangedHandler != null)
            {
                _dateForegroundDescriptor?.RemoveValueChanged(_dateTimeText, _themeChangedHandler);
            }
            if (_backgroundBorder != null && _themeChangedHandler != null)
            {
                _bgBorderDescriptor?.RemoveValueChanged(_backgroundBorder, _themeChangedHandler);
            }

            if (_wrapperPanel != null && _hostStackPanel != null)
            {
                _hostStackPanel.Children.Remove(_wrapperPanel);
            }
        }

        private const uint WmMouseMove = 0x0200;
        private const uint WmLButtonDown = 0x0201;
        private const uint WmLButtonUp = 0x0202;
        private const uint WmLButtonDblClk = 0x0203;
        private const uint WmRButtonDown = 0x0204;
        private const uint WmRButtonUp = 0x0205;
        private const uint WmContextMenu = 0x007B;
        private const uint NinSelect = 0x0400;
        private const int SwShow = 5;
        private const int SwRestore = 9;
        private static readonly IntPtr HwndMessage = new(-3);

        internal static string NormalizePath(string path)
        {
            string trimmed = path.Trim().Trim('"');
            try
            {
                return Path.GetFullPath(trimmed).TrimEnd('\\');
            }
            catch
            {
                return trimmed;
            }
        }

        internal static string StripExe(string name)
        {
            string trimmed = name.Trim();
            return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? trimmed[..^4]
                : trimmed;
        }

        internal static double GetDpiScaleForWindow(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero)
            {
                try
                {
                    uint dpi = GetDpiForWindow(hwnd);
                    if (dpi >= 72 && dpi <= 480)
                    {
                        return dpi / 96.0;
                    }
                }
                catch
                {
                }
            }
            return 1.0;
        }

        internal static string GetWindowClassNameSafe(IntPtr hwnd)
        {
            var sb = new StringBuilder(128);
            return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
        }

        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        private const uint GuiInMenuMode = 0x00000004;
        private const uint GuiPopupMenuMode = 0x00000010;

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(uint dwProcessId);

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam);
    }

    internal sealed class PinnedTrayEntry
    {
        public string PinId { get; set; } = string.Empty;
        public string ExecutablePath { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? RegistrySubkeyName { get; set; }
        public uint? NotifyIconUid { get; set; }
        public string? IconGuid { get; set; }
        public string? TooltipHint { get; set; }
        public string? CachedIconPngBase64 { get; set; }
        public int Order { get; set; }

        public PinnedTrayEntry Clone() => new()
        {
            PinId = PinId,
            ExecutablePath = ExecutablePath,
            ProcessName = ProcessName,
            DisplayName = DisplayName,
            RegistrySubkeyName = RegistrySubkeyName,
            NotifyIconUid = NotifyIconUid,
            IconGuid = IconGuid,
            TooltipHint = TooltipHint,
            CachedIconPngBase64 = CachedIconPngBase64,
            Order = Order
        };
    }

    internal static class PinnedTrayStore
    {
        private static readonly object Sync = new();
        private static readonly string DefaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "taskbarAndAIbar",
            "pinned-tray-icons.json");

        private static string _storePath = DefaultPath;
        private static List<PinnedTrayEntry> _entries = new();
        private static bool _loaded;

        public static event Action? StoreChanged;

        public static int Count
        {
            get
            {
                lock (Sync)
                {
                    EnsureLoadedLocked();
                    return _entries.Count;
                }
            }
        }

        internal static void UseCustomPathForTests(string? customPath)
        {
            lock (Sync)
            {
                _storePath = string.IsNullOrWhiteSpace(customPath) ? DefaultPath : customPath;
                _entries = new List<PinnedTrayEntry>();
                _loaded = false;
            }
        }

        public static IReadOnlyList<PinnedTrayEntry> GetSnapshot()
        {
            lock (Sync)
            {
                EnsureLoadedLocked();
                return _entries.Select(e => e.Clone()).ToArray();
            }
        }

        public static void UpsertAndPersist(PinnedTrayEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.PinId))
            {
                return;
            }

            lock (Sync)
            {
                EnsureLoadedLocked();
                int idx = FindMatchingIndexLocked(entry);
                if (idx >= 0)
                {
                    entry.Order = _entries[idx].Order;
                    if (string.IsNullOrWhiteSpace(entry.CachedIconPngBase64))
                    {
                        entry.CachedIconPngBase64 = _entries[idx].CachedIconPngBase64;
                    }
                    _entries[idx] = entry.Clone();
                }
                else
                {
                    entry.Order = _entries.Count;
                    _entries.Add(entry.Clone());
                }

                NormalizeOrderLocked();
                SaveLocked();
            }

            StoreChanged?.Invoke();
        }

        public static bool Remove(string pinId)
        {
            if (string.IsNullOrWhiteSpace(pinId))
            {
                return false;
            }

            bool removed;
            lock (Sync)
            {
                EnsureLoadedLocked();
                int idx = _entries.FindIndex(e => string.Equals(e.PinId, pinId, StringComparison.Ordinal));
                if (idx < 0)
                {
                    return false;
                }

                _entries.RemoveAt(idx);
                NormalizeOrderLocked();
                SaveLocked();
                removed = true;
            }

            if (removed)
            {
                StoreChanged?.Invoke();
            }
            return removed;
        }

        public static void MoveBeforeOrAfter(string draggedPinId, string targetPinId)
        {
            bool changed = false;
            lock (Sync)
            {
                EnsureLoadedLocked();
                int fromIdx = _entries.FindIndex(e => string.Equals(e.PinId, draggedPinId, StringComparison.Ordinal));
                int toIdx = _entries.FindIndex(e => string.Equals(e.PinId, targetPinId, StringComparison.Ordinal));
                if (fromIdx >= 0 && toIdx >= 0 && fromIdx != toIdx)
                {
                    PinnedTrayEntry moved = _entries[fromIdx];
                    _entries.RemoveAt(fromIdx);
                    _entries.Insert(toIdx, moved);
                    NormalizeOrderLocked();
                    SaveLocked();
                    changed = true;
                }
            }

            if (changed)
            {
                StoreChanged?.Invoke();
            }
        }

        public static void UpdateMetadataSilent(string pinId, string? displayName, string? iconBase64)
        {
            lock (Sync)
            {
                EnsureLoadedLocked();
                int idx = _entries.FindIndex(e => string.Equals(e.PinId, pinId, StringComparison.Ordinal));
                if (idx < 0)
                {
                    return;
                }

                bool dirty = false;
                if (!string.IsNullOrWhiteSpace(displayName) && !string.Equals(_entries[idx].DisplayName, displayName, StringComparison.Ordinal))
                {
                    _entries[idx].DisplayName = displayName;
                    _entries[idx].TooltipHint = displayName;
                    dirty = true;
                }

                if (!string.IsNullOrWhiteSpace(iconBase64) && !string.Equals(_entries[idx].CachedIconPngBase64, iconBase64, StringComparison.Ordinal))
                {
                    _entries[idx].CachedIconPngBase64 = iconBase64;
                    dirty = true;
                }

                if (dirty)
                {
                    SaveLocked();
                }
            }
        }

        private static int FindMatchingIndexLocked(PinnedTrayEntry candidate)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                PinnedTrayEntry cur = _entries[i];
                if (string.Equals(cur.PinId, candidate.PinId, StringComparison.Ordinal))
                {
                    return i;
                }

                if (!string.IsNullOrWhiteSpace(cur.RegistrySubkeyName) &&
                    string.Equals(cur.RegistrySubkeyName, candidate.RegistrySubkeyName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        private static void NormalizeOrderLocked()
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                _entries[i].Order = i;
            }
        }

        private static void EnsureLoadedLocked()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            _entries = new List<PinnedTrayEntry>();

            try
            {
                if (!File.Exists(_storePath))
                {
                    return;
                }

                string json = File.ReadAllText(_storePath, Encoding.UTF8);
                var parsed = JsonSerializer.Deserialize<List<PinnedTrayEntry>>(json);
                if (parsed != null)
                {
                    _entries = parsed
                        .Where(e => e != null && !string.IsNullOrWhiteSpace(e.PinId))
                        .OrderBy(e => e.Order)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"Failed to load {_storePath}: {ex.Message}");
            }
        }

        private static void SaveLocked()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_storePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
                string tmp = _storePath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                File.Move(tmp, _storePath, overwrite: true);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"Failed to save {_storePath}: {ex.Message}");
            }
        }
    }

    internal sealed class PinnedTrayItemViewModel : INotifyPropertyChanged
    {
        private string _displayName;
        private ImageSource? _iconSource;
        private BitmapSource? _originalBitmap;
        private BitmapSource? _lightBarBitmap;
        private BitmapSource? _darkBarBitmap;
        private bool _isMonochrome;
        private string? _rawIconHash;

        public event PropertyChangedEventHandler? PropertyChanged;

        public PinnedTrayItemViewModel(PinnedTrayEntry entry, bool useDarkForeground)
        {
            Entry = entry.Clone();
            _displayName = entry.DisplayName;
            RebuildIconFromBase64(entry.CachedIconPngBase64, useDarkForeground);
        }

        public PinnedTrayEntry Entry { get; private set; }
        public string PinId => Entry.PinId;
        public bool IsRunning { get; set; }
        public IntPtr LiveTrayHwnd { get; private set; }
        public uint? LiveCallbackMessage { get; private set; }
        public bool? LiveIsV4 { get; private set; }
        public uint? LiveUid { get; private set; }

        public string DisplayName
        {
            get => _displayName;
            private set
            {
                if (!string.Equals(_displayName, value, StringComparison.Ordinal))
                {
                    _displayName = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
                }
            }
        }

        public ImageSource? IconSource
        {
            get => _iconSource;
            private set
            {
                if (!ReferenceEquals(_iconSource, value))
                {
                    _iconSource = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconSource)));
                }
            }
        }

        public void UpdateFromEntry(PinnedTrayEntry entry, bool useDarkForeground, bool forceIconRebuild)
        {
            Entry = entry.Clone();
            DisplayName = entry.DisplayName;
            if (forceIconRebuild || _originalBitmap == null)
            {
                RebuildIconFromBase64(entry.CachedIconPngBase64, useDarkForeground);
            }
        }

        public void ApplyLiveState(LiveItemState state, bool useDarkForeground)
        {
            LiveTrayHwnd = state.TrayHwnd;
            LiveCallbackMessage = state.CallbackMessage;
            LiveIsV4 = state.IsV4;
            if (state.LiveUid.HasValue)
            {
                LiveUid = state.LiveUid;
            }

            if (!string.IsNullOrWhiteSpace(state.LiveTooltip))
            {
                DisplayName = state.LiveTooltip;
            }

            if (state.LiveIconPng is { Length: > 0 } png &&
                !string.Equals(state.LiveIconHash, _rawIconHash, StringComparison.Ordinal))
            {
                RebuildIconFromBytes(png, state.LiveIconHash, useDarkForeground);
                string? b64 = Convert.ToBase64String(png);
                PinnedTrayStore.UpdateMetadataSilent(PinId, state.LiveTooltip, b64);
            }
            else if (!string.IsNullOrWhiteSpace(state.LiveTooltip))
            {
                PinnedTrayStore.UpdateMetadataSilent(PinId, state.LiveTooltip, null);
            }
        }

        public void ApplyTheme(bool useDarkForeground)
        {
            if (_originalBitmap == null)
            {
                return;
            }

            if (!_isMonochrome)
            {
                IconSource = _originalBitmap;
                return;
            }

            IconSource = useDarkForeground
                ? (_lightBarBitmap ?? _originalBitmap)
                : (_darkBarBitmap ?? _originalBitmap);
        }

        private void RebuildIconFromBase64(string? base64, bool useDarkForeground)
        {
            if (string.IsNullOrWhiteSpace(base64))
            {
                return;
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                string hash = TrayIconBitmapHelper.ComputeHash(bytes);
                RebuildIconFromBytes(bytes, hash, useDarkForeground);
            }
            catch
            {
            }
        }

        private void RebuildIconFromBytes(byte[] pngBytes, string? hash, bool useDarkForeground)
        {
            BitmapSource? raw = TrayIconBitmapHelper.DecodePng(pngBytes);
            if (raw == null)
            {
                return;
            }

            _rawIconHash = hash ?? TrayIconBitmapHelper.ComputeHash(pngBytes);
            var adapted = TrayIconBitmapHelper.BuildThemeVariants(raw);
            _originalBitmap = adapted.Original;
            _lightBarBitmap = adapted.ForLightBar;
            _darkBarBitmap = adapted.ForDarkBar;
            _isMonochrome = adapted.IsMonochrome;

            ApplyTheme(useDarkForeground);
        }
    }

    internal sealed class LiveItemState
    {
        public bool IsRunning { get; init; }
        public string? LiveTooltip { get; init; }
        public byte[]? LiveIconPng { get; init; }
        public string? LiveIconHash { get; init; }
        public IntPtr TrayHwnd { get; init; }
        public uint? CallbackMessage { get; init; }
        public bool? IsV4 { get; init; }
        public uint? LiveUid { get; init; }
    }

    internal sealed class LiveTraySnapshot
    {
        public Dictionary<string, LiveItemState> States { get; } = new(StringComparer.Ordinal);
    }

    internal sealed class OverflowDropCandidate
    {
        public string? DisplayName { get; init; }
        public string? ExecutablePath { get; init; }
        public string? ProcessName { get; init; }
        public string? RegistrySubkeyName { get; init; }
        public uint? NotifyIconUid { get; init; }
        public Guid? IconGuid { get; init; }
        public byte[]? IconPngBytes { get; init; }

        public string BuildPinId()
        {
            if (!string.IsNullOrWhiteSpace(RegistrySubkeyName))
            {
                return $"subkey:{RegistrySubkeyName}";
            }

            if (!string.IsNullOrWhiteSpace(ExecutablePath))
            {
                string norm = HiddenTrayPinEnhancer.NormalizePath(ExecutablePath).ToLowerInvariant();
                string uidPart = NotifyIconUid.HasValue ? NotifyIconUid.Value.ToString() : (IconGuid?.ToString("D") ?? string.Empty);
                return string.IsNullOrEmpty(uidPart) ? $"exe:{norm}" : $"exe:{norm}|uid:{uidPart}";
            }

            return $"tip:{(DisplayName ?? string.Empty).Trim().ToLowerInvariant()}";
        }
    }

    internal static class NotifyIconCatalog
    {
        private const string NotifyIconSettingsSubKey = @"Control Panel\NotifyIconSettings";
        private static readonly Dictionary<string, (string? ProductName, string? FileDesc)> FileMetaCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object MetaSync = new();

        internal sealed class RegistryEntry
        {
            public string SubkeyName { get; init; } = string.Empty;
            public string ExecutablePath { get; init; } = string.Empty;
            public string ProcessBaseName { get; init; } = string.Empty;
            public string InitialTooltip { get; init; } = string.Empty;
            public uint? Uid { get; init; }
            public Guid? IconGuid { get; init; }
            public byte[]? IconSnapshot { get; init; }
        }

        internal sealed class RunningProcessInfo
        {
            public uint Pid { get; init; }
            public string ProcessName { get; init; } = string.Empty;
            public string? ExecutablePath { get; init; }
            public string? MainWindowTitle { get; init; }
        }

        public static List<RegistryEntry> ReadRegistryEntries()
        {
            var result = new List<RegistryEntry>();
            try
            {
                using RegistryKey? root = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsSubKey);
                if (root == null)
                {
                    return result;
                }

                foreach (string subName in root.GetSubKeyNames())
                {
                    using RegistryKey? sub = root.OpenSubKey(subName);
                    if (sub == null)
                    {
                        continue;
                    }

                    RegistryEntry? entry = ReadRegistryEntryFromSubkey(sub, subName);
                    if (entry != null)
                    {
                        result.Add(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"ReadRegistryEntries failed: {ex.Message}");
            }

            return result;
        }

        private static RegistryEntry? ReadSingleRegistryEntry(string subName)
        {
            if (string.IsNullOrWhiteSpace(subName))
            {
                return null;
            }

            try
            {
                using RegistryKey? root = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsSubKey);
                using RegistryKey? sub = root?.OpenSubKey(subName);
                return sub != null ? ReadRegistryEntryFromSubkey(sub, subName) : null;
            }
            catch
            {
                return null;
            }
        }

        private static RegistryEntry? ReadRegistryEntryFromSubkey(RegistryKey sub, string subName)
        {
            string? rawExe = sub.GetValue("ExecutablePath") as string;
            if (string.IsNullOrWhiteSpace(rawExe))
            {
                return null;
            }

            string expanded = ExpandKnownFolderPath(rawExe);
            string procBase = string.Empty;
            try
            {
                procBase = Path.GetFileNameWithoutExtension(expanded);
            }
            catch
            {
            }

            uint? uid = null;
            object? uidVal = sub.GetValue("UID");
            if (uidVal is int iUid && iUid >= 0)
            {
                uid = (uint)iUid;
            }
            else if (uidVal is long lUid && lUid >= 0 && lUid <= uint.MaxValue)
            {
                uid = (uint)lUid;
            }

            Guid? iconGuid = null;
            if (sub.GetValue("IconGuid") is string gStr && Guid.TryParse(gStr, out Guid parsed))
            {
                iconGuid = parsed;
            }

            return new RegistryEntry
            {
                SubkeyName = subName,
                ExecutablePath = expanded,
                ProcessBaseName = procBase,
                InitialTooltip = (sub.GetValue("InitialTooltip") as string ?? string.Empty).Trim(),
                Uid = uid,
                IconGuid = iconGuid,
                IconSnapshot = sub.GetValue("IconSnapshot") as byte[]
            };
        }

        private static List<RunningProcessInfo> SnapshotRunningProcessesForEntries(IReadOnlyList<PinnedTrayEntry> entries)
        {
            var procNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PinnedTrayEntry entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.ProcessName))
                {
                    string clean = HiddenTrayPinEnhancer.StripExe(entry.ProcessName);
                    if (!string.IsNullOrEmpty(clean))
                    {
                        procNames.Add(clean);
                    }
                }

                if (!string.IsNullOrWhiteSpace(entry.ExecutablePath))
                {
                    try
                    {
                        string exeBase = Path.GetFileNameWithoutExtension(entry.ExecutablePath);
                        if (!string.IsNullOrWhiteSpace(exeBase))
                        {
                            procNames.Add(exeBase);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            var list = new List<RunningProcessInfo>();
            foreach (string procName in procNames)
            {
                try
                {
                    foreach (Process p in Process.GetProcessesByName(procName))
                    {
                        using (p)
                        {
                            try
                            {
                                list.Add(new RunningProcessInfo
                                {
                                    Pid = (uint)p.Id,
                                    ProcessName = p.ProcessName,
                                    ExecutablePath = TryGetProcessPathSafe(p)
                                });
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            return list;
        }

        public static List<RunningProcessInfo> SnapshotRunningProcesses()
        {
            var list = new List<RunningProcessInfo>();
            foreach (Process p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        string? path = TryGetProcessPathSafe(p);
                        string? title = null;
                        try
                        {
                            title = p.MainWindowTitle;
                        }
                        catch
                        {
                        }

                        list.Add(new RunningProcessInfo
                        {
                            Pid = (uint)p.Id,
                            ProcessName = p.ProcessName,
                            ExecutablePath = path,
                            MainWindowTitle = title
                        });
                    }
                    catch
                    {
                    }
                }
            }
            return list;
        }

        private static readonly Dictionary<string, string[]> KnownProcessAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Weixin"] = new[] { "微信", "WeChat" },
            ["WeChat"] = new[] { "微信", "Weixin" },
            ["Feishu"] = new[] { "飞书", "Lark" },
            ["Lark"] = new[] { "飞书", "Feishu" },
            ["DingTalk"] = new[] { "钉钉" },
            ["WXWork"] = new[] { "企业微信", "WeCom" },
            ["WeMeetApp"] = new[] { "腾讯会议", "WeMeet" },
            ["quark"] = new[] { "夸克", "夸克网盘" },
            ["clouddrive_desktop_widget"] = new[] { "夸克网盘", "夸克" },
            ["BaiduNetdisk"] = new[] { "百度网盘" },
            ["BaiduNetdiskUnite"] = new[] { "百度网盘" },
            ["cloudmusic"] = new[] { "网易云音乐" },
            ["QQ"] = new[] { "QQ" },
            ["PixPin"] = new[] { "PixPin" },
            ["Everything"] = new[] { "Everything" },
            ["OneDrive"] = new[] { "OneDrive" },
            ["ONENOTEM"] = new[] { "OneNote", "快速笔记", "新建快速笔记" },
            ["Thunder"] = new[] { "迅雷" },
            ["JianyingPro"] = new[] { "剪映" },
            ["Doubao"] = new[] { "豆包" },
            ["ControlCenterTrayUI"] = new[] { "荣耀控制中心", "控制中心" },
            ["PCManagerTray"] = new[] { "荣耀电脑管家", "电脑管家" },
            ["HnOfficeCenterTray"] = new[] { "荣耀超级工作台", "超级工作台" },
            ["AISearchUI"] = new[] { "智慧搜索" },
            ["AIAssistantMain"] = new[] { "荣耀 YOYO 助理", "YOYO 助理" }
        };

        public static OverflowDropCandidate ResolveOverflowCandidate(
            string? uiaButtonName,
            string? uiaAutomationId,
            byte[]? capturedButtonPng,
            List<RegistryEntry>? regOverride = null,
            List<RunningProcessInfo>? procOverride = null)
        {
            string cleanTip = NormalizeTooltip(uiaButtonName);
            List<RegistryEntry> regEntries = regOverride ?? ReadRegistryEntries();
            List<RunningProcessInfo> runningProcs = procOverride ?? SnapshotRunningProcesses();
            Dictionary<uint, List<string>>? pidWindowTitles = procOverride == null && !string.IsNullOrEmpty(cleanTip)
                ? SnapshotProcessWindowTitles(runningProcs)
                : null;

            var runningByPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var runningByName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var titlesByNormPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var titlesByProcName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (RunningProcessInfo rp in runningProcs)
            {
                List<string> combinedTitles = CollectProcessTitles(rp, pidWindowTitles);
                if (!string.IsNullOrWhiteSpace(rp.ExecutablePath))
                {
                    string np = HiddenTrayPinEnhancer.NormalizePath(rp.ExecutablePath);
                    runningByPath.Add(np);
                    if (combinedTitles.Count > 0)
                    {
                        if (!titlesByNormPath.TryGetValue(np, out List<string>? list))
                        {
                            list = new List<string>();
                            titlesByNormPath[np] = list;
                        }
                        list.AddRange(combinedTitles);
                    }
                }
                if (!string.IsNullOrWhiteSpace(rp.ProcessName))
                {
                    runningByName.Add(rp.ProcessName);
                    if (combinedTitles.Count > 0)
                    {
                        if (!titlesByProcName.TryGetValue(rp.ProcessName, out List<string>? list))
                        {
                            list = new List<string>();
                            titlesByProcName[rp.ProcessName] = list;
                        }
                        list.AddRange(combinedTitles);
                    }
                }
            }

            RegistryEntry? bestReg = null;
            int bestScore = 0;
            int bestTextScore = 0;
            double bestVisualSim = 0.0;
            double secondVisualSimDiffExe = 0.0;

            foreach (RegistryEntry re in regEntries)
            {
                string normRegExe = string.IsNullOrWhiteSpace(re.ExecutablePath)
                    ? string.Empty
                    : HiddenTrayPinEnhancer.NormalizePath(re.ExecutablePath);
                bool isRunning = (!string.IsNullOrEmpty(normRegExe) && runningByPath.Contains(normRegExe)) ||
                                 (!string.IsNullOrWhiteSpace(re.ProcessBaseName) && runningByName.Contains(re.ProcessBaseName));

                List<string>? procTitles = null;
                if (!string.IsNullOrEmpty(normRegExe) && titlesByNormPath.TryGetValue(normRegExe, out List<string>? pList))
                {
                    procTitles = pList;
                }
                else if (!string.IsNullOrWhiteSpace(re.ProcessBaseName) && titlesByProcName.TryGetValue(re.ProcessBaseName, out List<string>? nList))
                {
                    procTitles = nList;
                }

                ScoreRegistryCandidateDetailed(
                    re,
                    cleanTip,
                    uiaAutomationId,
                    isRunning,
                    capturedButtonPng,
                    procTitles,
                    out int score,
                    out int textScore,
                    out double visualSim);

                if (score > bestScore)
                {
                    if (bestReg != null &&
                        !string.Equals(bestReg.ExecutablePath, re.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        secondVisualSimDiffExe = Math.Max(secondVisualSimDiffExe, bestVisualSim);
                    }
                    bestScore = score;
                    bestTextScore = textScore;
                    bestVisualSim = visualSim;
                    bestReg = re;
                }
                else if (bestReg == null ||
                         !string.Equals(bestReg.ExecutablePath, re.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    secondVisualSimDiffExe = Math.Max(secondVisualSimDiffExe, visualSim);
                }
            }

            // Anti-false-positive guard:
            // When text/GUID didn't match at all (e.g. localized/blank UIA tooltip), never let bare
            // isRunning (+30) or low visual similarity fabricate a random app like DingTalk.
            bool acceptRegistry = false;
            if (bestReg != null && bestScore >= 40)
            {
                if (bestTextScore > 0)
                {
                    acceptRegistry = true;
                }
                else
                {
                    bool visualClear = bestVisualSim >= 0.88 ||
                                       (bestVisualSim >= 0.80 && bestVisualSim - secondVisualSimDiffExe >= 0.06);
                    // If cleanTip was explicitly read from UIA and conflicts with the registry entry's own tooltip,
                    // require near-identical visual confirmation before overriding.
                    bool hasConflictingExplicitTip = !string.IsNullOrEmpty(cleanTip) &&
                                                     !string.IsNullOrEmpty(bestReg.InitialTooltip);
                    acceptRegistry = hasConflictingExplicitTip
                        ? bestVisualSim >= 0.90
                        : visualClear;
                }
            }

            if (acceptRegistry && bestReg != null)
            {
                byte[]? iconPng = bestReg.IconSnapshot is { Length: > 0 }
                    ? bestReg.IconSnapshot
                    : (ExtractExeIconPng(bestReg.ExecutablePath) ?? capturedButtonPng);

                return new OverflowDropCandidate
                {
                    DisplayName = !string.IsNullOrWhiteSpace(cleanTip)
                        ? cleanTip
                        : (!string.IsNullOrWhiteSpace(bestReg.InitialTooltip) ? bestReg.InitialTooltip : bestReg.ProcessBaseName),
                    ExecutablePath = bestReg.ExecutablePath,
                    ProcessName = bestReg.ProcessBaseName,
                    RegistrySubkeyName = bestReg.SubkeyName,
                    NotifyIconUid = bestReg.Uid,
                    IconGuid = bestReg.IconGuid,
                    IconPngBytes = iconPng
                };
            }

            // Fallback: match directly against running processes (process name, metadata, aliases, window titles, or EXE icon).
            RunningProcessInfo? bestProc = MatchRunningProcessDirectly(runningProcs, cleanTip, capturedButtonPng, pidWindowTitles);
            if (bestProc != null)
            {
                byte[]? iconPng = capturedButtonPng ?? ExtractExeIconPng(bestProc.ExecutablePath);
                return new OverflowDropCandidate
                {
                    DisplayName = !string.IsNullOrWhiteSpace(cleanTip) ? cleanTip : bestProc.ProcessName,
                    ExecutablePath = bestProc.ExecutablePath,
                    ProcessName = bestProc.ProcessName,
                    IconPngBytes = iconPng
                };
            }

            return new OverflowDropCandidate
            {
                DisplayName = !string.IsNullOrWhiteSpace(cleanTip) ? cleanTip : "托盘应用",
                IconPngBytes = capturedButtonPng
            };
        }

        internal static int ScoreRegistryCandidate(
            RegistryEntry re,
            string cleanTip,
            string? automationId,
            bool isRunning,
            byte[]? capturedButtonPng)
        {
            ScoreRegistryCandidateDetailed(
                re,
                cleanTip,
                automationId,
                isRunning,
                capturedButtonPng,
                null,
                out int score,
                out _,
                out _);
            return score;
        }

        private static void ScoreRegistryCandidateDetailed(
            RegistryEntry re,
            string cleanTip,
            string? automationId,
            bool isRunning,
            byte[]? capturedButtonPng,
            IReadOnlyList<string>? runningWindowTitles,
            out int totalScore,
            out int textOrGuidScore,
            out double visualSim)
        {
            int textScore = 0;

            string regTip = NormalizeTooltip(re.InitialTooltip);
            if (!string.IsNullOrEmpty(cleanTip) && !string.IsNullOrEmpty(regTip))
            {
                if (string.Equals(cleanTip, regTip, StringComparison.OrdinalIgnoreCase))
                {
                    textScore += 120;
                }
                else if (cleanTip.StartsWith(regTip, StringComparison.OrdinalIgnoreCase) ||
                         regTip.StartsWith(cleanTip, StringComparison.OrdinalIgnoreCase))
                {
                    textScore += 95;
                }
                else if (cleanTip.Contains(regTip, StringComparison.OrdinalIgnoreCase) ||
                         regTip.Contains(cleanTip, StringComparison.OrdinalIgnoreCase))
                {
                    textScore += 65;
                }
            }

            if (!string.IsNullOrEmpty(cleanTip) && !string.IsNullOrEmpty(re.ProcessBaseName))
            {
                string normTip = NormalizeToken(cleanTip);
                string normExe = NormalizeToken(re.ProcessBaseName);
                if (string.Equals(cleanTip, re.ProcessBaseName, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(normTip) && string.Equals(normTip, normExe, StringComparison.OrdinalIgnoreCase)))
                {
                    textScore += 110;
                }
                else if (cleanTip.Contains(re.ProcessBaseName, StringComparison.OrdinalIgnoreCase) ||
                         (!string.IsNullOrEmpty(normExe) && normTip.Contains(normExe, StringComparison.OrdinalIgnoreCase)))
                {
                    textScore += 75;
                }
                else if (KnownProcessAliases.TryGetValue(re.ProcessBaseName, out string[]? aliases))
                {
                    foreach (string alias in aliases)
                    {
                        if (string.Equals(cleanTip, alias, StringComparison.OrdinalIgnoreCase))
                        {
                            textScore += 105;
                            break;
                        }
                        if (cleanTip.Contains(alias, StringComparison.OrdinalIgnoreCase) ||
                            alias.Contains(cleanTip, StringComparison.OrdinalIgnoreCase))
                        {
                            textScore += 80;
                            break;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(cleanTip) && !string.IsNullOrEmpty(re.ExecutablePath))
            {
                var (prod, desc) = GetFileVersionMeta(re.ExecutablePath);
                foreach (string? label in new[] { prod, desc })
                {
                    if (string.IsNullOrWhiteSpace(label))
                    {
                        continue;
                    }

                    string cleanLabel = label.Trim();
                    if (string.Equals(cleanTip, cleanLabel, StringComparison.OrdinalIgnoreCase))
                    {
                        textScore += 105;
                        break;
                    }
                    if (cleanTip.Contains(cleanLabel, StringComparison.OrdinalIgnoreCase) ||
                        cleanLabel.Contains(cleanTip, StringComparison.OrdinalIgnoreCase))
                    {
                        textScore += 70;
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(cleanTip) && runningWindowTitles != null)
            {
                foreach (string wt in runningWindowTitles)
                {
                    string cleanWt = NormalizeTooltip(wt);
                    if (string.IsNullOrEmpty(cleanWt))
                    {
                        continue;
                    }

                    if (string.Equals(cleanTip, cleanWt, StringComparison.OrdinalIgnoreCase))
                    {
                        textScore += 90;
                        break;
                    }
                    if (cleanTip.Contains(cleanWt, StringComparison.OrdinalIgnoreCase) ||
                        cleanWt.Contains(cleanTip, StringComparison.OrdinalIgnoreCase))
                    {
                        textScore += 65;
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(automationId) && re.IconGuid.HasValue &&
                automationId.Contains(re.IconGuid.Value.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                textScore += 130;
            }

            visualSim = 0.0;
            int visualScore = 0;
            if (isRunning && capturedButtonPng is { Length: > 0 })
            {
                byte[]? refPng = re.IconSnapshot is { Length: > 0 }
                    ? re.IconSnapshot
                    : ExtractExeIconPng(re.ExecutablePath);
                if (refPng is { Length: > 0 })
                {
                    visualSim = TrayIconBitmapHelper.ComputeVisualSimilarity(capturedButtonPng, refPng);
                    if (visualSim >= 0.85)
                    {
                        visualScore = (int)Math.Round(visualSim * 115.0);
                    }
                    else if (visualSim >= 0.72)
                    {
                        visualScore = (int)Math.Round((visualSim - 0.50) * 160.0);
                    }
                }
            }

            textOrGuidScore = textScore;
            totalScore = (isRunning ? 30 : 0) + textScore + visualScore;
        }

        private static RunningProcessInfo? MatchRunningProcessDirectly(
            List<RunningProcessInfo> procs,
            string cleanTip,
            byte[]? capturedButtonPng,
            Dictionary<uint, List<string>>? pidWindowTitles)
        {
            string normTip = NormalizeToken(cleanTip);
            RunningProcessInfo? best = null;
            int bestScore = 0;
            double bestSim = 0.0;

            foreach (RunningProcessInfo rp in procs)
            {
                int score = 0;
                if (!string.IsNullOrEmpty(cleanTip))
                {
                    if (string.Equals(cleanTip, rp.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(normTip) && string.Equals(normTip, NormalizeToken(rp.ProcessName), StringComparison.OrdinalIgnoreCase)))
                    {
                        score = 100;
                    }
                    else if (KnownProcessAliases.TryGetValue(rp.ProcessName, out string[]? aliases) &&
                             aliases.Any(a => string.Equals(cleanTip, a, StringComparison.OrdinalIgnoreCase) ||
                                              cleanTip.Contains(a, StringComparison.OrdinalIgnoreCase)))
                    {
                        score = 92;
                    }
                    else
                    {
                        foreach (string title in CollectProcessTitles(rp, pidWindowTitles))
                        {
                            string cleanWt = NormalizeTooltip(title);
                            if (string.IsNullOrEmpty(cleanWt))
                            {
                                continue;
                            }

                            if (string.Equals(cleanTip, cleanWt, StringComparison.OrdinalIgnoreCase))
                            {
                                score = Math.Max(score, 90);
                                break;
                            }
                            if (cleanTip.Contains(cleanWt, StringComparison.OrdinalIgnoreCase) ||
                                cleanWt.Contains(cleanTip, StringComparison.OrdinalIgnoreCase))
                            {
                                score = Math.Max(score, 70);
                            }
                        }
                    }

                    if (score < 85 && !string.IsNullOrWhiteSpace(rp.ExecutablePath))
                    {
                        var (prod, desc) = GetFileVersionMeta(rp.ExecutablePath);
                        if ((!string.IsNullOrWhiteSpace(prod) && cleanTip.Contains(prod, StringComparison.OrdinalIgnoreCase)) ||
                            (!string.IsNullOrWhiteSpace(desc) && cleanTip.Contains(desc, StringComparison.OrdinalIgnoreCase)))
                        {
                            score = Math.Max(score, 85);
                        }
                    }
                }

                if (score == 0 && capturedButtonPng is { Length: > 0 } &&
                    !string.IsNullOrWhiteSpace(rp.ExecutablePath) &&
                    !IsNonTrayHostProcessName(rp.ProcessName))
                {
                    byte[]? exeIcon = ExtractExeIconPng(rp.ExecutablePath);
                    if (exeIcon is { Length: > 0 })
                    {
                        double sim = TrayIconBitmapHelper.ComputeVisualSimilarity(capturedButtonPng, exeIcon);
                        if (sim >= 0.88 && sim > bestSim)
                        {
                            bestSim = sim;
                            score = (int)Math.Round(sim * 95.0);
                        }
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = rp;
                }
            }

            return bestScore >= 60 ? best : null;
        }

        private static bool IsNonTrayHostProcessName(string? procName) =>
            string.Equals(procName, "dotnet", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(procName, "conhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(procName, "cmd", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(procName, "powershell", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(procName, "pwsh", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(procName, "svchost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(procName, "dllhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(procName, "OpenConsole", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(procName, "RuntimeBroker", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(procName, "WmiPrvSE", StringComparison.OrdinalIgnoreCase);

        private static List<string> CollectProcessTitles(
            RunningProcessInfo rp,
            Dictionary<uint, List<string>>? pidWindowTitles)
        {
            var titles = new List<string>();
            if (!string.IsNullOrWhiteSpace(rp.MainWindowTitle))
            {
                titles.Add(rp.MainWindowTitle);
            }
            if (pidWindowTitles != null && rp.Pid != 0 &&
                pidWindowTitles.TryGetValue(rp.Pid, out List<string>? extra))
            {
                titles.AddRange(extra);
            }
            return titles;
        }

        private static Dictionary<uint, List<string>> SnapshotProcessWindowTitles(List<RunningProcessInfo> procs)
        {
            var result = new Dictionary<uint, List<string>>();
            var pids = new HashSet<uint>();
            foreach (RunningProcessInfo rp in procs)
            {
                if (rp.Pid != 0)
                {
                    pids.Add(rp.Pid);
                }
            }

            if (pids.Count == 0)
            {
                return result;
            }

            EnumWindows((hwnd, lParam) =>
            {
                _ = GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0 || !pids.Contains(pid))
                {
                    return true;
                }

                int len = GetWindowTextLength(hwnd);
                if (len <= 0 || len > 160)
                {
                    return true;
                }

                var sb = new StringBuilder(len + 1);
                if (GetWindowText(hwnd, sb, sb.Capacity) > 0)
                {
                    string text = sb.ToString().Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (!result.TryGetValue(pid, out List<string>? list))
                        {
                            list = new List<string>();
                            result[pid] = list;
                        }
                        list.Add(text);
                    }
                }
                return true;
            }, IntPtr.Zero);

            return result;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(HiddenTrayPinEnhancer.EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        public static LiveTraySnapshot BuildLiveSnapshot(IReadOnlyList<PinnedTrayEntry> entries)
        {
            var result = new LiveTraySnapshot();
            if (entries.Count == 0)
            {
                return result;
            }

            List<RunningProcessInfo> runningProcs = SnapshotRunningProcessesForEntries(entries);
            List<RegistryEntry>? fullRegEntries = null;
            Dictionary<string, RegistryEntry>? directRegCache = null;
            IReadOnlyList<OverflowButtonSnapshot> overflowButtons = OverflowDragCoordinator.GetLastKnownOverflowButtons();

            foreach (PinnedTrayEntry entry in entries)
            {
                string normExe = string.IsNullOrWhiteSpace(entry.ExecutablePath)
                    ? string.Empty
                    : HiddenTrayPinEnhancer.NormalizePath(entry.ExecutablePath);
                string procBase = !string.IsNullOrWhiteSpace(entry.ProcessName)
                    ? HiddenTrayPinEnhancer.StripExe(entry.ProcessName)
                    : (!string.IsNullOrWhiteSpace(normExe) ? Path.GetFileNameWithoutExtension(normExe) : string.Empty);

                bool isRunning = runningProcs.Any(rp =>
                    (!string.IsNullOrEmpty(normExe) && !string.IsNullOrWhiteSpace(rp.ExecutablePath) &&
                     string.Equals(HiddenTrayPinEnhancer.NormalizePath(rp.ExecutablePath), normExe, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(procBase) &&
                     string.Equals(rp.ProcessName, procBase, StringComparison.OrdinalIgnoreCase)));

                RegistryEntry? bestReg = null;
                if (!string.IsNullOrEmpty(normExe) || !string.IsNullOrEmpty(procBase))
                {
                    if (!string.IsNullOrWhiteSpace(entry.RegistrySubkeyName))
                    {
                        directRegCache ??= new Dictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase);
                        if (!directRegCache.TryGetValue(entry.RegistrySubkeyName, out bestReg))
                        {
                            bestReg = ReadSingleRegistryEntry(entry.RegistrySubkeyName);
                            if (bestReg != null)
                            {
                                directRegCache[entry.RegistrySubkeyName] = bestReg;
                            }
                        }
                    }

                    if (bestReg == null && !string.IsNullOrEmpty(normExe))
                    {
                        fullRegEntries ??= ReadRegistryEntries();
                        bestReg = fullRegEntries.FirstOrDefault(r =>
                            string.Equals(HiddenTrayPinEnhancer.NormalizePath(r.ExecutablePath), normExe, StringComparison.OrdinalIgnoreCase) &&
                            (!entry.NotifyIconUid.HasValue || r.Uid == entry.NotifyIconUid));

                        if (bestReg == null)
                        {
                            RegistryEntry? singleMatch = null;
                            int matchCount = 0;
                            foreach (RegistryEntry r in fullRegEntries)
                            {
                                if (string.Equals(HiddenTrayPinEnhancer.NormalizePath(r.ExecutablePath), normExe, StringComparison.OrdinalIgnoreCase))
                                {
                                    singleMatch = r;
                                    matchCount++;
                                    if (matchCount > 1)
                                    {
                                        singleMatch = null;
                                        break;
                                    }
                                }
                            }
                            bestReg = singleMatch;
                        }
                    }
                }

                // Keep tooltip-only fallback pins visible on the TopBar even when no EXE path was resolvable,
                // so dragging arbitrary protected/unknown tray icons doesn't silently vanish right after drop.
                bool effectiveRunning = isRunning || (string.IsNullOrEmpty(normExe) && string.IsNullOrEmpty(procBase));
                RegistryEntry? reg = bestReg;

                byte[]? livePng = reg?.IconSnapshot;
                string? liveHash = livePng is { Length: > 0 } ? TrayIconBitmapHelper.ComputeHash(livePng) : null;

                string? liveTip = null;
                if (overflowButtons.Count > 0)
                {
                    // Match open overflow button tooltip updates (e.g. "OneDrive - 正在同步")
                    foreach (OverflowButtonSnapshot ob in overflowButtons)
                    {
                        if (!string.IsNullOrWhiteSpace(ob.Name) &&
                            MatchesEntryTooltip(entry, ob.Name))
                        {
                            liveTip = NormalizeTooltip(ob.Name);
                            break;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(liveTip) && reg != null && !string.IsNullOrWhiteSpace(reg.InitialTooltip))
                {
                    liveTip = NormalizeTooltip(reg.InitialTooltip);
                }

                result.States[entry.PinId] = new LiveItemState
                {
                    IsRunning = effectiveRunning,
                    LiveTooltip = liveTip,
                    LiveIconPng = livePng,
                    LiveIconHash = liveHash,
                    LiveUid = reg?.Uid
                };
            }

            return result;
        }

        private static bool MatchesEntryTooltip(PinnedTrayEntry entry, string candidateName)
        {
            string clean = NormalizeTooltip(candidateName);
            if (string.IsNullOrEmpty(clean))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(entry.TooltipHint) &&
                (string.Equals(clean, entry.TooltipHint, StringComparison.OrdinalIgnoreCase) ||
                 clean.StartsWith(entry.TooltipHint, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            string? procBase = !string.IsNullOrWhiteSpace(entry.ProcessName)
                ? entry.ProcessName
                : Path.GetFileNameWithoutExtension(entry.ExecutablePath);
            return !string.IsNullOrWhiteSpace(procBase) &&
                   clean.StartsWith(procBase, StringComparison.OrdinalIgnoreCase);
        }

        internal static string NormalizeTooltip(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            string firstLine = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? raw;
            return firstLine.Trim();
        }

        private static string NormalizeToken(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                }
            }
            return sb.ToString();
        }

        private static (string? ProductName, string? FileDesc) GetFileVersionMeta(string path)
        {
            lock (MetaSync)
            {
                if (FileMetaCache.TryGetValue(path, out var cached))
                {
                    return cached;
                }
            }

            (string? ProductName, string? FileDesc) meta = (null, null);
            try
            {
                if (File.Exists(path))
                {
                    var fvi = FileVersionInfo.GetVersionInfo(path);
                    meta = (fvi.ProductName?.Trim(), fvi.FileDescription?.Trim());
                }
            }
            catch
            {
            }

            lock (MetaSync)
            {
                FileMetaCache[path] = meta;
            }
            return meta;
        }

        public static string ExpandKnownFolderPath(string rawPath)
        {
            string trimmed = rawPath.Trim().Trim('"');
            if (trimmed.Length > 38 && trimmed[0] == '{')
            {
                int closeIdx = trimmed.IndexOf('}');
                if (closeIdx > 0 && Guid.TryParse(trimmed.Substring(0, closeIdx + 1), out Guid folderGuid))
                {
                    string rest = trimmed[(closeIdx + 1)..].TrimStart('\\', '/');
                    IntPtr pPath = IntPtr.Zero;
                    try
                    {
                        if (SHGetKnownFolderPath(ref folderGuid, 0, IntPtr.Zero, out pPath) == 0 && pPath != IntPtr.Zero)
                        {
                            string baseDir = Marshal.PtrToStringUni(pPath) ?? string.Empty;
                            if (!string.IsNullOrEmpty(baseDir))
                            {
                                return Path.Combine(baseDir, rest);
                            }
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        if (pPath != IntPtr.Zero)
                        {
                            Marshal.FreeCoTaskMem(pPath);
                        }
                    }
                }
            }

            return Environment.ExpandEnvironmentVariables(trimmed);
        }

        public static string? TryGetProcessPathSafe(Process p)
        {
            try
            {
                IntPtr hProc = OpenProcess(ProcessQueryLimitedInformation, false, (uint)p.Id);
                if (hProc != IntPtr.Zero)
                {
                    try
                    {
                        var sb = new StringBuilder(1024);
                        int cap = sb.Capacity;
                        if (QueryFullProcessImageName(hProc, 0, sb, ref cap) && cap > 0)
                        {
                            return sb.ToString(0, cap);
                        }
                    }
                    finally
                    {
                        CloseHandle(hProc);
                    }
                }
            }
            catch
            {
            }

            try
            {
                return p.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }

        public static byte[]? ExtractExeIconPng(string? exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return null;
            }

            var shfi = new SHFILEINFO();
            IntPtr res = SHGetFileInfo(exePath, 0, ref shfi, Marshal.SizeOf<SHFILEINFO>(), ShgfiIcon | ShgfiLargeIcon);
            if (res == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                BitmapSource bmp = Imaging.CreateBitmapSourceFromHIcon(
                    shfi.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(32, 32));
                bmp.Freeze();
                return TrayIconBitmapHelper.EncodePng(bmp);
            }
            catch
            {
                return null;
            }
            finally
            {
                DestroyIcon(shfi.hIcon);
            }
        }

        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint ShgfiIcon = 0x000000100;
        private const uint ShgfiLargeIcon = 0x000000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, int cbFileInfo, uint uFlags);

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);
    }

    internal static class TrayIconBitmapHelper
    {
        private const byte DarkInkR = 0x1F;
        private const byte DarkInkG = 0x1F;
        private const byte DarkInkB = 0x1F;

        public static string ComputeHash(byte[] data)
        {
            return Convert.ToHexString(SHA256.HashData(data));
        }

        public static byte[]? EncodePng(BitmapSource source)
        {
            try
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(source));
                using var ms = new MemoryStream();
                enc.Save(ms);
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        public static BitmapSource? DecodePng(byte[] pngBytes)
        {
            try
            {
                using var ms = new MemoryStream(pngBytes);
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = ms;
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch
            {
                return null;
            }
        }

        public static (BitmapSource Original, BitmapSource ForLightBar, BitmapSource ForDarkBar, bool IsMonochrome) BuildThemeVariants(BitmapSource raw)
        {
            BitmapSource pbgra = raw.Format == PixelFormats.Pbgra32
                ? raw
                : new FormatConvertedBitmap(raw, PixelFormats.Pbgra32, null, 0);
            if (!pbgra.IsFrozen)
            {
                pbgra.Freeze();
            }

            int w = pbgra.PixelWidth;
            int h = pbgra.PixelHeight;
            if (w <= 0 || h <= 0)
            {
                return (pbgra, pbgra, pbgra, false);
            }

            int stride = w * 4;
            byte[] pixels = new byte[stride * h];
            pbgra.CopyPixels(pixels, stride, 0);

            int opaque = 0;
            int colorful = 0;
            double lumSum = 0.0;

            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                byte a = pixels[i + 3];
                if (a < 32)
                {
                    continue;
                }

                // Un-premultiply for color saturation inspection
                byte b = (byte)Math.Min(255, pixels[i] * 255 / a);
                byte g = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
                byte r = (byte)Math.Min(255, pixels[i + 2] * 255 / a);

                int max = Math.Max(r, Math.Max(g, b));
                int min = Math.Min(r, Math.Min(g, b));
                if (max - min > 28)
                {
                    colorful++;
                }

                lumSum += (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
                opaque++;
            }

            if (opaque == 0)
            {
                return (pbgra, pbgra, pbgra, false);
            }

            bool isMonochrome = (colorful / (double)opaque) <= 0.06;
            if (!isMonochrome)
            {
                // Preserve original colors on both light and dark TopBars.
                return (pbgra, pbgra, pbgra, false);
            }

            double avgLum = lumSum / opaque;
            BitmapSource forLightBar = avgLum > 0.55
                ? CreateTintedBitmap(pixels, w, h, stride, DarkInkR, DarkInkG, DarkInkB)
                : pbgra;
            BitmapSource forDarkBar = avgLum < 0.45
                ? CreateTintedBitmap(pixels, w, h, stride, 0xFF, 0xFF, 0xFF)
                : pbgra;

            return (pbgra, forLightBar, forDarkBar, true);
        }

        private static BitmapSource CreateTintedBitmap(byte[] srcPixels, int w, int h, int stride, byte r, byte g, byte b)
        {
            byte[] dst = new byte[srcPixels.Length];
            for (int i = 0; i + 3 < srcPixels.Length; i += 4)
            {
                byte a = srcPixels[i + 3];
                dst[i + 3] = a;
                if (a > 0)
                {
                    dst[i + 0] = (byte)(b * a / 255);
                    dst[i + 1] = (byte)(g * a / 255);
                    dst[i + 2] = (byte)(r * a / 255);
                }
            }

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, dst, stride);
            bmp.Freeze();
            return bmp;
        }

        public static byte[]? CaptureScreenRectAsPng(Rect boundsPx)
        {
            int x = (int)Math.Round(boundsPx.X);
            int y = (int)Math.Round(boundsPx.Y);
            int w = Math.Clamp((int)Math.Round(boundsPx.Width), 12, 128);
            int h = Math.Clamp((int)Math.Round(boundsPx.Height), 12, 128);

            // Crop inner ~60% icon core so outer button padding doesn't dominate
            int insetX = Math.Max(0, w / 6);
            int insetY = Math.Max(0, h / 6);
            int cw = Math.Max(8, w - insetX * 2);
            int ch = Math.Max(8, h - insetY * 2);
            int cx = x + insetX;
            int cy = y + insetY;

            IntPtr screenDc = IntPtr.Zero;
            IntPtr memDc = IntPtr.Zero;
            IntPtr hBmp = IntPtr.Zero;
            IntPtr oldBmp = IntPtr.Zero;
            try
            {
                screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero)
                {
                    return null;
                }

                memDc = CreateCompatibleDC(screenDc);
                hBmp = CreateCompatibleBitmap(screenDc, cw, ch);
                if (memDc == IntPtr.Zero || hBmp == IntPtr.Zero)
                {
                    return null;
                }

                oldBmp = SelectObject(memDc, hBmp);
                if (!BitBlt(memDc, 0, 0, cw, ch, screenDc, cx, cy, Srccopy))
                {
                    return null;
                }

                var bi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = cw,
                        biHeight = -ch,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0
                    }
                };

                byte[] buf = new byte[cw * ch * 4];
                if (GetDIBits(memDc, hBmp, 0, (uint)ch, buf, ref bi, 0) == 0)
                {
                    return null;
                }

                // Sample outer corner pixel as uniform background and zero out matching background RGBA cleanly
                // so empty background color doesn't corrupt cropped visual similarity.
                int trIdx = Math.Max(0, cw - 1) * 4;
                int blIdx = Math.Max(0, ch - 1) * cw * 4;
                byte bgB = buf[0];
                byte bgG = buf[1];
                byte bgR = buf[2];
                if (cw > 2 && ch > 2 &&
                    Math.Abs(buf[trIdx] - buf[blIdx]) <= 8 &&
                    Math.Abs(buf[trIdx + 1] - buf[blIdx + 1]) <= 8 &&
                    Math.Abs(buf[trIdx + 2] - buf[blIdx + 2]) <= 8 &&
                    (Math.Abs(buf[trIdx] - bgB) > 12 || Math.Abs(buf[trIdx + 1] - bgG) > 12 || Math.Abs(buf[trIdx + 2] - bgR) > 12))
                {
                    bgB = buf[trIdx];
                    bgG = buf[trIdx + 1];
                    bgR = buf[trIdx + 2];
                }

                for (int i = 0; i + 3 < buf.Length; i += 4)
                {
                    int db = Math.Abs(buf[i] - bgB);
                    int dg = Math.Abs(buf[i + 1] - bgG);
                    int dr = Math.Abs(buf[i + 2] - bgR);
                    int maxD = Math.Max(dr, Math.Max(dg, db));
                    if (maxD <= 14)
                    {
                        buf[i] = 0;
                        buf[i + 1] = 0;
                        buf[i + 2] = 0;
                        buf[i + 3] = 0;
                    }
                    else
                    {
                        buf[i + 3] = 255;
                    }
                }

                var bmp = BitmapSource.Create(cw, ch, 96, 96, PixelFormats.Bgra32, null, buf, cw * 4);
                bmp.Freeze();
                return EncodePng(bmp);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (oldBmp != IntPtr.Zero && memDc != IntPtr.Zero)
                {
                    SelectObject(memDc, oldBmp);
                }
                if (hBmp != IntPtr.Zero)
                {
                    DeleteObject(hBmp);
                }
                if (memDc != IntPtr.Zero)
                {
                    DeleteDC(memDc);
                }
                if (screenDc != IntPtr.Zero)
                {
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }

        public static double ComputeVisualSimilarity(byte[] pngA, byte[] pngB)
        {
            BitmapSource? a = DecodePng(pngA);
            BitmapSource? b = DecodePng(pngB);
            if (a == null || b == null)
            {
                return 0.0;
            }

            float[]? vecA = ExtractNormalized16x16(a);
            float[]? vecB = ExtractNormalized16x16(b);
            if (vecA == null || vecB == null)
            {
                return 0.0;
            }

            double diffSum = 0.0;
            for (int i = 0; i < vecA.Length; i++)
            {
                diffSum += Math.Abs(vecA[i] - vecB[i]);
            }

            double avgDiff = diffSum / vecA.Length;
            return Math.Clamp(1.0 - avgDiff * 1.4, 0.0, 1.0);
        }

        private static float[]? ExtractNormalized16x16(BitmapSource src)
        {
            var bgra = src.Format == PixelFormats.Bgra32
                ? src
                : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

            int w = bgra.PixelWidth;
            int h = bgra.PixelHeight;
            if (w <= 0 || h <= 0)
            {
                return null;
            }

            byte[] px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);

            int minX = w;
            int minY = h;
            int maxX = -1;
            int maxY = -1;
            int opaqueCount = 0;
            bool hasTransparent = false;
            bool hasColorVariation = false;
            byte firstB = 0, firstG = 0, firstR = 0;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = (y * w + x) * 4;
                    if (px[idx + 3] >= 24)
                    {
                        if (opaqueCount == 0)
                        {
                            firstB = px[idx];
                            firstG = px[idx + 1];
                            firstR = px[idx + 2];
                        }
                        else if (!hasColorVariation &&
                                 (Math.Abs(px[idx] - firstB) > 8 ||
                                  Math.Abs(px[idx + 1] - firstG) > 8 ||
                                  Math.Abs(px[idx + 2] - firstR) > 8))
                        {
                            hasColorVariation = true;
                        }
                        opaqueCount++;
                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                    }
                    else
                    {
                        hasTransparent = true;
                        px[idx] = 0;
                        px[idx + 1] = 0;
                        px[idx + 2] = 0;
                        px[idx + 3] = 0;
                    }
                }
            }

            if (maxX < minX || maxY < minY || (!hasTransparent && !hasColorVariation))
            {
                return null;
            }

            int cw = maxX - minX + 1;
            int ch = maxY - minY + 1;
            var clean = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            var cropped = new CroppedBitmap(clean, new Int32Rect(minX, minY, cw, ch));
            var scaled = new TransformedBitmap(cropped, new ScaleTransform(16.0 / cw, 16.0 / ch));
            byte[] outPx = new byte[16 * 16 * 4];
            scaled.CopyPixels(outPx, 16 * 4, 0);

            float[] outVec = new float[outPx.Length];
            for (int i = 0; i < outPx.Length; i++)
            {
                outVec[i] = outPx[i] / 255.0f;
            }
            return outVec;
        }

        private const uint Srccopy = 0x00CC0020;

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, byte[] lpvBits, ref BITMAPINFO lpbmi, uint usage);
    }

    internal sealed class OverflowButtonSnapshot
    {
        public string Name { get; init; } = string.Empty;
        public string AutomationId { get; init; } = string.Empty;
        public Rect BoundsPx { get; init; }
    }

    internal sealed class TrayDragGhostWindow : Window
    {
        private readonly Image _iconImage;
        private readonly TextBlock _labelText;
        private readonly Border _pillBorder;
        private IntPtr _hwnd;

        public TrayDragGhostWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            IsHitTestVisible = false;
            SizeToContent = SizeToContent.WidthAndHeight;

            _iconImage = new Image
            {
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(_iconImage, BitmapScalingMode.HighQuality);

            _labelText = new TextBlock
            {
                FontSize = 11.5,
                Foreground = Brushes.White,
                Margin = new Thickness(6, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 160,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _iconImage, _labelText }
            };

            _pillBorder = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 5, 8, 5),
                Background = new SolidColorBrush(Color.FromArgb(0xDC, 0x1E, 0x20, 0x26)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = row
            };

            Content = _pillBorder;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            if (_hwnd != IntPtr.Zero)
            {
                int exStyle = GetWindowLong(_hwnd, GwlExstyle);
                SetWindowLong(_hwnd, GwlExstyle, exStyle | WsExTransparent | WsExToolwindow | WsExNoactivate);
            }
        }

        public void ShowAtPhysicalPixel(ImageSource? icon, string label, bool isWarningOrRemove, int xPx, int yPx)
        {
            _iconImage.Source = icon;
            _iconImage.Visibility = icon != null ? Visibility.Visible : Visibility.Collapsed;
            _labelText.Text = label;
            _pillBorder.BorderBrush = new SolidColorBrush(isWarningOrRemove
                ? Color.FromArgb(0xC0, 0xFF, 0x5A, 0x5A)
                : Color.FromArgb(0x88, 0x5B, 0x9B, 0xFF));

            if (_hwnd == IntPtr.Zero)
            {
                _hwnd = new WindowInteropHelper(this).EnsureHandle();
            }

            double scale = HiddenTrayPinEnhancer.GetDpiScaleForWindow(_hwnd);
            int offsetPx = Math.Max(10, (int)Math.Round(14.0 * scale));

            if (!IsVisible)
            {
                Left = xPx / scale + 14.0;
                Top = yPx / scale + 14.0;
                Show();
            }

            if (_hwnd != IntPtr.Zero)
            {
                SetWindowPos(_hwnd, HwndTopmost, xPx + offsetPx, yPx + offsetPx, 0, 0, SwpNosize | SwpNoactivate);
            }
        }

        private const int GwlExstyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolwindow = 0x00000080;
        private const int WsExNoactivate = 0x08000000;
        private const uint SwpNosize = 0x0001;
        private const uint SwpNoactivate = 0x0010;
        private static readonly IntPtr HwndTopmost = new(-1);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    }

    internal static class OverflowDragCoordinator
    {
        private sealed class ActiveDragSession
        {
            public HiddenTrayPinEnhancer.POINT StartPt;
            public IntPtr OverflowHwnd;
            public HiddenTrayPinEnhancer.RECT OverflowRect;
            public OverflowButtonSnapshot? InitialHitButton;
            public byte[]? CapturedIconPng;
            public OverflowDropCandidate? ResolvedCandidate;
            public bool DragStarted;
            public int Committed;
            public int Cancelled;
        }

        private static readonly object Sync = new();
        private static readonly HashSet<HiddenTrayPinEnhancer> Enhancers = new();
        private static readonly List<OverflowButtonSnapshot> CachedButtons = new();

        private static Dispatcher? _uiDispatcher;
        private static TrayDragGhostWindow? _ghostWindow;
        private static HookProc? _mouseHookDelegate;
        private static IntPtr _mouseHookHandle;
        private static WinEventDelegate? _winEventDelegate;
        private static IntPtr _winEventHook;
        private static EventInfo? _trayManagerVisibilityEvent;
        private static EventHandler? _trayManagerVisibilityHandler;
        private static bool _hooksInitialized;

        private static IntPtr _activeOverflowHwnd;
        private static IntPtr _cachedOverflowHwnd;
        private static int _scanGeneration;
        private static HiddenTrayPinEnhancer.RECT _activeOverflowRect;
        private static ActiveDragSession? _activeDrag;
        private static int _moveUpdateQueued;

        public static bool HasActiveOverflowDrag
        {
            get
            {
                ActiveDragSession? session = _activeDrag;
                return session != null &&
                       session.DragStarted &&
                       Volatile.Read(ref session.Committed) == 0 &&
                       Volatile.Read(ref session.Cancelled) == 0;
            }
        }

        public static void RegisterEnhancer(HiddenTrayPinEnhancer enhancer)
        {
            lock (Sync)
            {
                Enhancers.Add(enhancer);
                _uiDispatcher = enhancer.TopBarWindow.Dispatcher;
                if (!_hooksInitialized)
                {
                    _hooksInitialized = true;
                    HookOverflowSignalsLocked();
                }
            }
        }

        public static void UnregisterEnhancer(HiddenTrayPinEnhancer enhancer)
        {
            TrayDragGhostWindow? ghostToClose = null;
            Dispatcher? ghostDispatcher = null;
            lock (Sync)
            {
                Enhancers.Remove(enhancer);
                if (Enhancers.Count > 0)
                {
                    if (ReferenceEquals(_uiDispatcher, enhancer.TopBarWindow.Dispatcher))
                    {
                        _uiDispatcher = Enhancers.First().TopBarWindow.Dispatcher;
                    }
                    return;
                }

                ShutdownCoordinatorLocked(out ghostToClose, out ghostDispatcher);
            }

            if (ghostToClose != null)
            {
                Action closeAction = () =>
                {
                    try
                    {
                        ghostToClose.Close();
                    }
                    catch
                    {
                    }
                };

                if (ghostDispatcher != null && !ghostDispatcher.HasShutdownStarted &&
                    ghostDispatcher.Thread != Thread.CurrentThread)
                {
                    ghostDispatcher.BeginInvoke(DispatcherPriority.Send, closeAction);
                }
                else
                {
                    closeAction();
                }
            }
        }

        public static IReadOnlyList<OverflowButtonSnapshot> GetLastKnownOverflowButtons()
        {
            lock (Sync)
            {
                return CachedButtons.ToArray();
            }
        }

        public static void TryCommitActiveOleDrop(HiddenTrayPinEnhancer targetEnhancer)
        {
            ActiveDragSession? session = _activeDrag;
            _activeDrag = null;
            if (session == null || Interlocked.Exchange(ref session.Committed, 1) == 1)
            {
                return;
            }

            _ = CommitSessionOnWorkerAsync(session, targetEnhancer);
        }

        public static void UpdateBarReorderGhost(ImageSource? icon, string label, bool isRemove, HiddenTrayPinEnhancer.POINT pt)
        {
            EnsureGhostWindow()?.ShowAtPhysicalPixel(icon, label, isRemove, pt.X, pt.Y);
        }

        public static void HideBarReorderGhost()
        {
            if (_ghostWindow != null && _ghostWindow.IsVisible)
            {
                _ghostWindow.Hide();
            }
        }

        private static void HookOverflowSignalsLocked()
        {
            try
            {
                Assembly? sharedAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "MagicTaskbar.Shared", StringComparison.OrdinalIgnoreCase));
                Type? trayMgrType = sharedAsm?.GetType("MagicTaskbarWpf.TrayManager");
                EventInfo? evt = trayMgrType?.GetEvent("OverflowVisibilityChanged", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (evt != null)
                {
                    _trayManagerVisibilityEvent = evt;
                    _trayManagerVisibilityHandler = (_, _) => OnOverflowSignal();
                    evt.AddEventHandler(null, _trayManagerVisibilityHandler);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"TrayManager.OverflowVisibilityChanged hook skipped: {ex.Message}");
            }

            try
            {
                _winEventDelegate = OnWinEvent;
                _winEventHook = SetWinEventHook(
                    EventObjectShow,
                    EventObjectHide,
                    IntPtr.Zero,
                    _winEventDelegate,
                    0,
                    0,
                    WinEventOutOfContext | WinEventSkipOwnProcess);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"Overflow WinEvent hook skipped: {ex.Message}");
            }
        }

        private static void OnWinEvent(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint idEventThread,
            uint dwmsEventTime)
        {
            if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero)
            {
                return;
            }

            string cls = HiddenTrayPinEnhancer.GetWindowClassNameSafe(hwnd);
            if (string.Equals(cls, "TopLevelWindowForOverflowXamlIsland", StringComparison.Ordinal) ||
                string.Equals(cls, "NotifyIconOverflowWindow", StringComparison.Ordinal))
            {
                OnOverflowSignal();
            }
        }

        private static void OnOverflowSignal()
        {
            Dispatcher? disp;
            lock (Sync)
            {
                if (Enhancers.Count == 0)
                {
                    return;
                }
                disp = _uiDispatcher;
            }

            if (disp == null || disp.HasShutdownStarted)
            {
                return;
            }

            disp.BeginInvoke(DispatcherPriority.Send, new Action(SyncOverflowMonitorState));
        }

        private static void SyncOverflowMonitorState()
        {
            bool hasEnhancers;
            lock (Sync)
            {
                hasEnhancers = Enhancers.Count > 0;
            }

            if (!hasEnhancers || !AdaptiveSettingsStore.IsHiddenTrayIconPinningEnabled)
            {
                UnhookMouse();
                return;
            }

            IntPtr ov = FindVisibleOverflowWindow();
            if (ov != IntPtr.Zero)
            {
                bool changedWindow = _activeOverflowHwnd != ov;
                bool emptyCache;
                lock (Sync)
                {
                    emptyCache = _cachedOverflowHwnd != ov || CachedButtons.Count == 0;
                }

                _activeOverflowHwnd = ov;
                HiddenTrayPinEnhancer.GetWindowRect(ov, out _activeOverflowRect);
                EnsureMouseHookInstalled();
                int gen = Interlocked.Increment(ref _scanGeneration);
                _ = ScanOverflowButtonsAsync(ov, gen);
                if (changedWindow || emptyCache)
                {
                    // Re-scan twice after XAML Island layout & ShowNativeOverflowBelow SetWindowPos settle (~90ms, ~250ms)
                    _ = Task.Run(async () =>
                    {
                        foreach (int delayMs in new[] { 90, 160 })
                        {
                            await Task.Delay(delayMs).ConfigureAwait(false);
                            if (Volatile.Read(ref _scanGeneration) != gen ||
                                _activeOverflowHwnd != ov ||
                                !IsWindowVisible(ov))
                            {
                                break;
                            }

                            HiddenTrayPinEnhancer.GetWindowRect(ov, out _activeOverflowRect);
                            await ScanOverflowButtonsAsync(ov, gen).ConfigureAwait(false);
                        }
                    });
                }
            }
            else if (_activeDrag == null)
            {
                _activeOverflowHwnd = IntPtr.Zero;
                Interlocked.Increment(ref _scanGeneration);
                lock (Sync)
                {
                    _cachedOverflowHwnd = IntPtr.Zero;
                    CachedButtons.Clear();
                }
                UnhookMouse();
            }
        }

        private static IntPtr FindVisibleOverflowWindow()
        {
            IntPtr win11 = FindWindow("TopLevelWindowForOverflowXamlIsland", null);
            if (win11 != IntPtr.Zero && IsWindowVisible(win11))
            {
                return win11;
            }

            IntPtr win10 = FindWindow("NotifyIconOverflowWindow", null);
            if (win10 != IntPtr.Zero && IsWindowVisible(win10))
            {
                return win10;
            }

            return IntPtr.Zero;
        }

        private static List<IntPtr> CollectOverflowWindowHandles(IntPtr overflowHwnd)
        {
            var handles = new List<IntPtr> { overflowHwnd };
            EnumChildWindows(overflowHwnd, (child, _) =>
            {
                if (child != IntPtr.Zero && !handles.Contains(child))
                {
                    handles.Add(child);
                }
                return true;
            }, IntPtr.Zero);
            return handles;
        }

        private static string ExtractAutomationElementLabel(AutomationElement el)
        {
            try
            {
                string name = (el.Current.Name ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }

                string help = (el.Current.HelpText ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(help))
                {
                    return help;
                }

                AutomationElementCollection children = el.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                for (int i = 0; i < children.Count && i < 8; i++)
                {
                    string childName = (children[i].Current.Name ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(childName))
                    {
                        return childName;
                    }
                }
            }
            catch
            {
            }
            return string.Empty;
        }

        private static Task ScanOverflowButtonsAsync(IntPtr overflowHwnd, int scanGen)
        {
            return Task.Run(() =>
            {
                var found = new List<OverflowButtonSnapshot>();
                var seenRects = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    System.Windows.Automation.Condition cond = new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));

                    foreach (IntPtr hwnd in CollectOverflowWindowHandles(overflowHwnd))
                    {
                        AutomationElement? root = null;
                        try
                        {
                            root = AutomationElement.FromHandle(hwnd);
                        }
                        catch
                        {
                        }

                        if (root == null)
                        {
                            continue;
                        }

                        AutomationElementCollection items = root.FindAll(TreeScope.Descendants, cond);
                        for (int i = 0; i < items.Count; i++)
                        {
                            try
                            {
                                AutomationElement el = items[i];
                                Rect rect = el.Current.BoundingRectangle;
                                if (rect.IsEmpty || rect.Width <= 2 || rect.Height <= 2 ||
                                    rect.Width > 140 || rect.Height > 140)
                                {
                                    continue;
                                }

                                // Allow transiently-offscreen items during XAML Island popup animation as long as they sit in valid bounds.
                                if (el.Current.IsOffscreen &&
                                    !_activeOverflowRect.Equals(default) &&
                                    (rect.Right < _activeOverflowRect.Left - 8 ||
                                     rect.Left > _activeOverflowRect.Right + 8 ||
                                     rect.Bottom < _activeOverflowRect.Top - 8 ||
                                     rect.Top > _activeOverflowRect.Bottom + 8))
                                {
                                    continue;
                                }

                                string name = ExtractAutomationElementLabel(el);
                                string autoId = (el.Current.AutomationId ?? string.Empty).Trim();
                                if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(autoId))
                                {
                                    continue;
                                }

                                string key = $"{(int)Math.Round(rect.Left / 4.0)}:{(int)Math.Round(rect.Top / 4.0)}:{(int)Math.Round(rect.Width / 4.0)}:{(int)Math.Round(rect.Height / 4.0)}";
                                if (!seenRects.Add(key))
                                {
                                    continue;
                                }

                                found.Add(new OverflowButtonSnapshot
                                {
                                    Name = name,
                                    AutomationId = autoId,
                                    BoundsPx = rect
                                });
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warn($"Overflow UIA scan error: {ex.Message}");
                }

                if (Volatile.Read(ref _scanGeneration) != scanGen && _activeOverflowHwnd != overflowHwnd)
                {
                    return;
                }

                lock (Sync)
                {
                    _cachedOverflowHwnd = overflowHwnd;
                    CachedButtons.Clear();
                    CachedButtons.AddRange(found);
                }

                // Trigger live tooltip update on enhancers when overflow opens
                List<HiddenTrayPinEnhancer> snapshotEnhancers;
                lock (Sync)
                {
                    snapshotEnhancers = Enhancers.ToList();
                }
                foreach (var enh in snapshotEnhancers)
                {
                    enh.QueueLiveRefresh();
                }
            });
        }

        private static void EnsureMouseHookInstalled()
        {
            if (_mouseHookHandle != IntPtr.Zero)
            {
                return;
            }

            _mouseHookDelegate ??= LowLevelMouseProc;
            using Process curProc = Process.GetCurrentProcess();
            using ProcessModule? mod = curProc.MainModule;
            IntPtr hMod = mod != null ? GetModuleHandle(mod.ModuleName) : IntPtr.Zero;
            _mouseHookHandle = SetWindowsHookEx(WhMouseLl, _mouseHookDelegate, hMod, 0);
        }

        private static void UnhookMouse()
        {
            if (_mouseHookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHookHandle);
                _mouseHookHandle = IntPtr.Zero;
            }
        }

        private static IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0 || !AdaptiveSettingsStore.IsHiddenTrayIconPinningEnabled)
            {
                return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
            }

            int msg = wParam.ToInt32();
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            if (msg == WmLButtonDown)
            {
                IntPtr ov = _activeOverflowHwnd;
                if (ov == IntPtr.Zero || !IsWindowVisible(ov))
                {
                    ov = FindVisibleOverflowWindow();
                    if (ov != IntPtr.Zero)
                    {
                        _activeOverflowHwnd = ov;
                    }
                }

                if (ov != IntPtr.Zero && IsWindowVisible(ov) &&
                    HiddenTrayPinEnhancer.GetWindowRect(ov, out HiddenTrayPinEnhancer.RECT r) &&
                    PointInRect(new HiddenTrayPinEnhancer.RECT { Left = r.Left - 4, Top = r.Top - 4, Right = r.Right + 4, Bottom = r.Bottom + 4 }, info.pt))
                {
                    _activeOverflowRect = r;
                    var session = new ActiveDragSession
                    {
                        StartPt = info.pt,
                        OverflowHwnd = ov,
                        OverflowRect = r,
                        InitialHitButton = null
                    };
                    _activeDrag = session;
                    _uiDispatcher?.BeginInvoke(DispatcherPriority.Send, new Action(() => BeginResolveSession(session)));
                }
            }
            else if (msg == WmMouseMove)
            {
                ActiveDragSession? session = _activeDrag;
                if (session != null)
                {
                    double scale = HiddenTrayPinEnhancer.GetDpiScaleForWindow(session.OverflowHwnd);
                    int threshPx = Math.Max(6, (int)Math.Round(5.0 * scale));
                    int dx = info.pt.X - session.StartPt.X;
                    int dy = info.pt.Y - session.StartPt.Y;

                    if (!session.DragStarted && dx * dx + dy * dy >= threshPx * threshPx)
                    {
                        session.DragStarted = true;
                    }

                    if (session.DragStarted &&
                        Volatile.Read(ref session.Committed) == 0 &&
                        Volatile.Read(ref session.Cancelled) == 0 &&
                        Interlocked.Exchange(ref _moveUpdateQueued, 1) == 0)
                    {
                        var ptCopy = info.pt;
                        _uiDispatcher?.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                        {
                            Interlocked.Exchange(ref _moveUpdateQueued, 0);
                            UpdateDragVisuals(session, ptCopy);
                        }));
                    }
                }
            }
            else if (msg == WmLButtonUp)
            {
                ActiveDragSession? session = _activeDrag;
                _activeDrag = null;
                if (session != null)
                {
                    HiddenTrayPinEnhancer? targetEnhancer = null;
                    bool overTopBar = session.DragStarted &&
                                      TryFindDropTargetEnhancer(info.pt, out targetEnhancer);
                    if (overTopBar && targetEnhancer != null &&
                        Interlocked.Exchange(ref session.Committed, 1) == 0)
                    {
                        // Cancel Win10 ToolbarWindow32 pressed button state if needed
                        if (string.Equals(HiddenTrayPinEnhancer.GetWindowClassNameSafe(session.OverflowHwnd), "NotifyIconOverflowWindow", StringComparison.Ordinal))
                        {
                            PostMessage(session.OverflowHwnd, WmCancelMode, UIntPtr.Zero, IntPtr.Zero);
                        }

                        _uiDispatcher?.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                        {
                            ClearAllDropHighlights();
                            HideBarReorderGhost();
                            _ = CommitSessionOnWorkerAsync(session, targetEnhancer);
                            SyncOverflowMonitorState();
                        }));
                    }
                    else
                    {
                        Interlocked.Exchange(ref session.Cancelled, 1);
                        _uiDispatcher?.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                        {
                            ClearAllDropHighlights();
                            HideBarReorderGhost();
                            SyncOverflowMonitorState();
                        }));
                    }
                }
            }
            else if (msg == WmRButtonDown)
            {
                ActiveDragSession? session = _activeDrag;
                _activeDrag = null;
                if (session != null)
                {
                    Interlocked.Exchange(ref session.Cancelled, 1);
                    _uiDispatcher?.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                    {
                        ClearAllDropHighlights();
                        HideBarReorderGhost();
                    }));
                }
            }

            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        private static OverflowButtonSnapshot? HitTestCachedButton(IntPtr overflowHwnd, HiddenTrayPinEnhancer.POINT pt)
        {
            lock (Sync)
            {
                if (_cachedOverflowHwnd != overflowHwnd)
                {
                    return null;
                }

                foreach (OverflowButtonSnapshot btn in CachedButtons)
                {
                    if (pt.X >= btn.BoundsPx.Left && pt.X <= btn.BoundsPx.Right &&
                        pt.Y >= btn.BoundsPx.Top && pt.Y <= btn.BoundsPx.Bottom)
                    {
                        return btn;
                    }
                }
            }
            return null;
        }

        private static void BeginResolveSession(ActiveDragSession session)
        {
            string hitSource = "none";
            if (IsWindowVisible(session.OverflowHwnd))
            {
                session.InitialHitButton = TryResolveButtonFromPoint(session.StartPt);
                if (session.InitialHitButton != null)
                {
                    hitSource = "live";
                }
            }

            if (session.InitialHitButton == null)
            {
                session.InitialHitButton = HitTestCachedButton(session.OverflowHwnd, session.StartPt);
                if (session.InitialHitButton != null)
                {
                    hitSource = "cached";
                }
            }

            if (session.InitialHitButton == null && IsWindowVisible(session.OverflowHwnd))
            {
                session.InitialHitButton = TryFindButtonInOverflowTree(session.OverflowHwnd, session.StartPt);
                if (session.InitialHitButton != null)
                {
                    hitSource = "tree";
                }
            }

            double scale = HiddenTrayPinEnhancer.GetDpiScaleForWindow(session.OverflowHwnd);
            OverflowButtonSnapshot? hit = session.InitialHitButton;
            bool validButtonRect = hit != null &&
                                   !hit.BoundsPx.IsEmpty &&
                                   hit.BoundsPx.Width >= 12 && hit.BoundsPx.Width <= 140 &&
                                   hit.BoundsPx.Height >= 12 && hit.BoundsPx.Height <= 140 &&
                                   session.StartPt.X >= hit.BoundsPx.Left - 4 && session.StartPt.X <= hit.BoundsPx.Right + 4 &&
                                   session.StartPt.Y >= hit.BoundsPx.Top - 4 && session.StartPt.Y <= hit.BoundsPx.Bottom + 4;

            if (validButtonRect && hit != null)
            {
                session.CapturedIconPng = TrayIconBitmapHelper.CaptureScreenRectAsPng(hit.BoundsPx);
            }
            else
            {
                int boxPx = Math.Max(36, (int)Math.Round(38.0 * scale));
                int half = boxPx / 2;
                Rect fallbackBox = new Rect(session.StartPt.X - half, session.StartPt.Y - half, boxPx, boxPx);
                session.CapturedIconPng = TrayIconBitmapHelper.CaptureScreenRectAsPng(fallbackBox);
            }

            _ = Task.Run(() =>
            {
                OverflowButtonSnapshot? btn = session.InitialHitButton;
                if (btn == null && IsWindowVisible(session.OverflowHwnd))
                {
                    btn = TryResolveButtonFromPoint(session.StartPt) ??
                          HitTestCachedButton(session.OverflowHwnd, session.StartPt) ??
                          TryFindButtonInOverflowTree(session.OverflowHwnd, session.StartPt);
                    session.InitialHitButton = btn;
                    if (btn != null && hitSource == "none")
                    {
                        hitSource = "deferred";
                    }
                }

                if (btn != null || session.CapturedIconPng is { Length: > 0 })
                {
                    OverflowDropCandidate resolved = NotifyIconCatalog.ResolveOverflowCandidate(
                        btn?.Name,
                        btn?.AutomationId,
                        session.CapturedIconPng);
                    session.ResolvedCandidate = resolved;
                    DiagnosticLog.Info(
                        $"OverflowDrag pre-resolved (src={hitSource}, tip='{btn?.Name ?? ""}', autoId='{btn?.AutomationId ?? ""}') -> " +
                        $"name='{resolved.DisplayName}', exe='{resolved.ExecutablePath ?? ""}', subkey='{resolved.RegistrySubkeyName ?? ""}'.");
                }
            });
        }

        private static OverflowButtonSnapshot? TryFindButtonInOverflowTree(IntPtr overflowHwnd, HiddenTrayPinEnhancer.POINT pt)
        {
            try
            {
                System.Windows.Automation.Condition cond = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));

                OverflowButtonSnapshot? best = null;
                double bestArea = double.MaxValue;

                foreach (IntPtr hwnd in CollectOverflowWindowHandles(overflowHwnd))
                {
                    AutomationElement? root = null;
                    try
                    {
                        root = AutomationElement.FromHandle(hwnd);
                    }
                    catch
                    {
                    }

                    if (root == null)
                    {
                        continue;
                    }

                    AutomationElementCollection items = root.FindAll(TreeScope.Descendants, cond);
                    for (int i = 0; i < items.Count; i++)
                    {
                        try
                        {
                            AutomationElement el = items[i];
                            Rect rect = el.Current.BoundingRectangle;
                            if (rect.IsEmpty || rect.Width <= 2 || rect.Height <= 2 ||
                                rect.Width > 140 || rect.Height > 140)
                            {
                                continue;
                            }

                            if (pt.X < rect.Left - 4 || pt.X > rect.Right + 4 ||
                                pt.Y < rect.Top - 4 || pt.Y > rect.Bottom + 4)
                            {
                                continue;
                            }

                            string name = ExtractAutomationElementLabel(el);
                            string autoId = (el.Current.AutomationId ?? string.Empty).Trim();
                            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(autoId))
                            {
                                continue;
                            }

                            double area = rect.Width * rect.Height;
                            if (area < bestArea)
                            {
                                bestArea = area;
                                best = new OverflowButtonSnapshot
                                {
                                    Name = name,
                                    AutomationId = autoId,
                                    BoundsPx = rect
                                };
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                return best;
            }
            catch
            {
                return null;
            }
        }

        private static OverflowButtonSnapshot? TryResolveButtonFromPoint(HiddenTrayPinEnhancer.POINT pt)
        {
            try
            {
                AutomationElement? el = AutomationElement.FromPoint(new Point(pt.X, pt.Y));
                int depth = 0;
                while (el != null && depth++ < 8)
                {
                    ControlType ct = el.Current.ControlType;
                    if (ct == ControlType.Button || ct == ControlType.ListItem)
                    {
                        Rect rect = el.Current.BoundingRectangle;
                        if (!rect.IsEmpty && rect.Width <= 140 && rect.Height <= 140)
                        {
                            return new OverflowButtonSnapshot
                            {
                                Name = ExtractAutomationElementLabel(el),
                                AutomationId = (el.Current.AutomationId ?? string.Empty).Trim(),
                                BoundsPx = rect
                            };
                        }
                    }
                    el = TreeWalker.ControlViewWalker.GetParent(el);
                }
            }
            catch
            {
            }
            return null;
        }

        private static void UpdateDragVisuals(ActiveDragSession session, HiddenTrayPinEnhancer.POINT pt)
        {
            if (!ReferenceEquals(_activeDrag, session) ||
                Volatile.Read(ref session.Committed) != 0 ||
                Volatile.Read(ref session.Cancelled) != 0)
            {
                return;
            }

            bool overTopBar = TryFindDropTargetEnhancer(pt, out HiddenTrayPinEnhancer? targetEnhancer);

            List<HiddenTrayPinEnhancer> enhancers;
            lock (Sync)
            {
                enhancers = Enhancers.ToList();
            }

            foreach (HiddenTrayPinEnhancer enh in enhancers)
            {
                enh.SetDropHighlight(overTopBar && ReferenceEquals(enh, targetEnhancer));
            }

            OverflowDropCandidate? cand = session.ResolvedCandidate;
            byte[]? png = cand?.IconPngBytes ?? session.CapturedIconPng;
            ImageSource? img = png != null ? TrayIconBitmapHelper.DecodePng(png) : null;
            string name = cand?.DisplayName ??
                          (!string.IsNullOrWhiteSpace(session.InitialHitButton?.Name) ? session.InitialHitButton!.Name : "固定到状态栏");
            string label = overTopBar ? $"松开固定 · {name}" : name;

            EnsureGhostWindow()?.ShowAtPhysicalPixel(img, label, false, pt.X, pt.Y);
        }

        private static async Task CommitSessionOnWorkerAsync(ActiveDragSession session, HiddenTrayPinEnhancer targetEnhancer)
        {
            try
            {
                OverflowDropCandidate? cand = session.ResolvedCandidate;
                if (cand == null)
                {
                    cand = await Task.Run(() =>
                    {
                        OverflowButtonSnapshot? btn = session.InitialHitButton ??
                                                      TryResolveButtonFromPoint(session.StartPt) ??
                                                      HitTestCachedButton(session.OverflowHwnd, session.StartPt) ??
                                                      (IsWindowVisible(session.OverflowHwnd)
                                                          ? TryFindButtonInOverflowTree(session.OverflowHwnd, session.StartPt)
                                                          : null);
                        if (btn == null && (session.CapturedIconPng == null || session.CapturedIconPng.Length == 0))
                        {
                            return null;
                        }

                        return NotifyIconCatalog.ResolveOverflowCandidate(
                            btn?.Name,
                            btn?.AutomationId,
                            session.CapturedIconPng);
                    });
                }

                if (cand == null)
                {
                    DiagnosticLog.Warn("Overflow drop ignored: unable to resolve clicked overflow tray button.");
                    return;
                }

                if (IsUnresolvedGenericCandidate(cand))
                {
                    DiagnosticLog.Warn($"Overflow drop ignored: generic fallback '{cand.DisplayName}' without executable or registry identity.");
                    return;
                }

                _uiDispatcher?.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    targetEnhancer.CommitDraggedOverflowCandidate(cand);
                }));
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"CommitSessionOnWorkerAsync error: {ex.Message}");
            }
        }

        internal static bool IsUnresolvedGenericCandidate(OverflowDropCandidate cand)
        {
            if (!string.IsNullOrWhiteSpace(cand.ExecutablePath) ||
                !string.IsNullOrWhiteSpace(cand.ProcessName) ||
                !string.IsNullOrWhiteSpace(cand.RegistrySubkeyName))
            {
                return false;
            }

            string name = (cand.DisplayName ?? string.Empty).Trim();
            return string.IsNullOrEmpty(name) ||
                   string.Equals(name, "托盘应用", StringComparison.Ordinal) ||
                   string.Equals(name, "固定到状态栏", StringComparison.Ordinal);
        }

        private static bool TryFindDropTargetEnhancer(HiddenTrayPinEnhancer.POINT pt, out HiddenTrayPinEnhancer? target)
        {
            target = null;
            List<HiddenTrayPinEnhancer> snapshot;
            lock (Sync)
            {
                snapshot = Enhancers.ToList();
            }

            foreach (HiddenTrayPinEnhancer enh in snapshot)
            {
                if (enh.TryGetDropZoneRectPx(out HiddenTrayPinEnhancer.RECT rectPx) && PointInRect(rectPx, pt))
                {
                    target = enh;
                    return true;
                }
            }
            return false;
        }

        private static void ClearAllDropHighlights()
        {
            List<HiddenTrayPinEnhancer> snapshot;
            lock (Sync)
            {
                snapshot = Enhancers.ToList();
            }
            foreach (HiddenTrayPinEnhancer enh in snapshot)
            {
                enh.SetDropHighlight(false);
            }
        }

        private static TrayDragGhostWindow? EnsureGhostWindow()
        {
            if (_uiDispatcher == null || _uiDispatcher.HasShutdownStarted)
            {
                return null;
            }

            _ghostWindow ??= new TrayDragGhostWindow();
            return _ghostWindow;
        }

        private static void ShutdownCoordinatorLocked(out TrayDragGhostWindow? ghostToClose, out Dispatcher? ghostDispatcher)
        {
            _hooksInitialized = false;
            ActiveDragSession? curDrag = _activeDrag;
            _activeDrag = null;
            if (curDrag != null)
            {
                Interlocked.Exchange(ref curDrag.Cancelled, 1);
            }
            _activeOverflowHwnd = IntPtr.Zero;
            _cachedOverflowHwnd = IntPtr.Zero;
            Interlocked.Increment(ref _scanGeneration);
            CachedButtons.Clear();
            Interlocked.Exchange(ref _moveUpdateQueued, 0);

            UnhookMouse();
            if (_winEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_winEventHook);
                _winEventHook = IntPtr.Zero;
            }
            _winEventDelegate = null;

            if (_trayManagerVisibilityEvent != null && _trayManagerVisibilityHandler != null)
            {
                try
                {
                    _trayManagerVisibilityEvent.RemoveEventHandler(null, _trayManagerVisibilityHandler);
                }
                catch
                {
                }
            }
            _trayManagerVisibilityEvent = null;
            _trayManagerVisibilityHandler = null;

            ghostToClose = _ghostWindow;
            _ghostWindow = null;
            ghostDispatcher = _uiDispatcher;
            _uiDispatcher = null;
        }

        private static bool PointInRect(HiddenTrayPinEnhancer.RECT r, HiddenTrayPinEnhancer.POINT pt)
        {
            return pt.X >= r.Left && pt.X <= r.Right && pt.Y >= r.Top && pt.Y <= r.Bottom;
        }

        private const int WhMouseLl = 14;
        private const int WmMouseMove = 0x0200;
        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonUp = 0x0202;
        private const int WmRButtonDown = 0x0204;
        private const uint WmCancelMode = 0x001F;
        private const uint EventObjectShow = 0x8002;
        private const uint EventObjectHide = 0x8003;
        private const uint WinEventOutOfContext = 0x0000;
        private const uint WinEventSkipOwnProcess = 0x0002;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public HiddenTrayPinEnhancer.POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, HiddenTrayPinEnhancer.EnumWindowsProc lpEnumFunc, IntPtr lParam);
    }
}

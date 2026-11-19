using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TopBarAdaptiveColor
{
    internal static class TopBarSettingsInjector
    {
        private const string ToolboxTabButtonName = "MagicToolboxTabButton";
        private const string ToolboxPanelName = "MagicToolboxPanel";
        private const int ToolboxTabIndex = 5;

        private static readonly ConditionalWeakTable<Window, SettingsToolboxHost> Hosts = new();

        public static void Attach(Window settingsWindow)
        {
            if (settingsWindow == null)
            {
                return;
            }

            try
            {
                if (Hosts.TryGetValue(settingsWindow, out var existingHost))
                {
                    existingHost.SyncTogglesFromStore();
                    return;
                }

                var host = SettingsToolboxHost.TryAttach(settingsWindow);
                if (host != null)
                {
                    Hosts.Add(settingsWindow, host);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"TopBarSettingsInjector.Attach failed: {ex}");
            }
        }

        private sealed class SettingsToolboxHost
        {
            private readonly Window _window;
            private readonly Button _toolboxButton;
            private readonly TextBlock _toolboxButtonText;
            private readonly ScrollViewer _toolboxPanel;
            private readonly Dictionary<string, CheckBox> _toggleControls = new(StringComparer.Ordinal);
            private readonly List<FrameworkElement> _nativePanels = new();
            private readonly MethodInfo? _showTabMethod;
            private readonly FieldInfo? _selectedTabBgField;
            private readonly FieldInfo? _currentTabField;

            private bool _isToolboxSelected;
            private bool _disposed;

            private SettingsToolboxHost(
                Window window,
                Button toolboxButton,
                TextBlock toolboxButtonText,
                ScrollViewer toolboxPanel,
                MethodInfo? showTabMethod,
                FieldInfo? selectedTabBgField,
                FieldInfo? currentTabField)
            {
                _window = window;
                _toolboxButton = toolboxButton;
                _toolboxButtonText = toolboxButtonText;
                _toolboxPanel = toolboxPanel;
                _showTabMethod = showTabMethod;
                _selectedTabBgField = selectedTabBgField;
                _currentTabField = currentTabField;
            }

            public static SettingsToolboxHost? TryAttach(Window settingsWindow)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                Type t = settingsWindow.GetType();

                var notificationPanel = t.GetField("NotificationPanel", flags)?.GetValue(settingsWindow) as FrameworkElement;
                var notificationCanvas = t.GetField("NotificationPanelCanvas", flags)?.GetValue(settingsWindow) as Canvas;
                var acrylicCheckBox = t.GetField("TopBarAcrylicCheckBox", flags)?.GetValue(settingsWindow) as CheckBox;
                var privacyTabButton = t.GetField("PrivacyTabButton", flags)?.GetValue(settingsWindow) as Button;
                var notificationTabButton = t.GetField("NotificationTabButton", flags)?.GetValue(settingsWindow) as Button;

                Button? anchorTabButton = privacyTabButton ?? notificationTabButton;
                FrameworkElement? anchorPanel = notificationPanel ?? (FrameworkElement?)notificationCanvas;

                if (anchorTabButton == null || anchorPanel == null)
                {
                    DiagnosticLog.Warn("TopBarSettingsInjector: Required sidebar tab button or content panel not found.");
                    return null;
                }

                // Idempotency guard in case Attach is called on a window already hosting our controls.
                if (LogicalTreeHelper.FindLogicalNode(settingsWindow, ToolboxTabButtonName) != null ||
                    FindDescendantByName<Button>(settingsWindow, ToolboxTabButtonName) != null)
                {
                    DiagnosticLog.Info("TopBarSettingsInjector: MagicToolbox tab already present.");
                    return null;
                }

                bool isChinese = DetectChineseLanguage(settingsWindow, t, flags);
                var privacyIcon = t.GetField("PrivacyTabIcon", flags)?.GetValue(settingsWindow) as FrameworkElement;
                var upgradeTabButton = t.GetField("UpgradeTabButton", flags)?.GetValue(settingsWindow) as Button;
                var showTabMethod = t.GetMethod("ShowTab", flags);
                var selectedTabBgField = t.GetField("_selectedTabBackgroundBrush", flags);
                var currentTabField = t.GetField("_currentTab", flags);

                var (tabButton, tabText) = CreateSidebarTabButton(anchorTabButton, privacyIcon, isChinese);
                if (!InsertSidebarButton(anchorTabButton, upgradeTabButton, tabButton))
                {
                    DiagnosticLog.Warn("TopBarSettingsInjector: Unable to place MagicToolbox sidebar tab.");
                    return null;
                }

                var (toolboxPanel, toggleControls) = BuildToolboxScrollPanel(settingsWindow, anchorPanel, notificationCanvas, acrylicCheckBox, isChinese);
                if (!InsertContentPanel(anchorPanel, toolboxPanel))
                {
                    DiagnosticLog.Warn("TopBarSettingsInjector: Unable to attach MagicToolbox content panel.");
                    return null;
                }

                var host = new SettingsToolboxHost(
                    settingsWindow,
                    tabButton,
                    tabText,
                    toolboxPanel,
                    showTabMethod,
                    selectedTabBgField,
                    currentTabField);

                foreach (var kv in toggleControls)
                {
                    host._toggleControls[kv.Key] = kv.Value;
                }

                host.HookNavigationAndLifecycle(settingsWindow, t, flags);
                DiagnosticLog.Info($"Injected modular MagicToolbox tab & page ({host._toggleControls.Count} toggles).");
                return host;
            }

            public void SyncTogglesFromStore()
            {
                if (_disposed)
                {
                    return;
                }

                foreach (var mod in ToolboxModuleRegistry.Modules)
                {
                    if (mod.ControlKind == ToolboxControlKind.Toggle &&
                        _toggleControls.TryGetValue(mod.Id, out CheckBox? cb))
                    {
                        bool val = AdaptiveSettingsStore.IsModuleEnabled(mod.Id, mod.DefaultEnabled);
                        if (cb.IsChecked != val)
                        {
                            cb.IsChecked = val;
                        }
                    }
                }
            }

            private void HookNavigationAndLifecycle(Window settingsWindow, Type t, BindingFlags flags)
            {
                _toolboxButton.Click += (_, _) => SelectToolboxTab();

                string[] nativeTabFields =
                {
                    "NotificationTabButton",
                    "TaskbarTabButton",
                    "PureModeTabButton",
                    "UpgradeTabButton",
                    "PrivacyTabButton",
                    "BackButton"
                };

                foreach (string name in nativeTabFields)
                {
                    if (t.GetField(name, flags)?.GetValue(settingsWindow) is Button btn)
                    {
                        btn.Click += OnNativeTabClicked;
                    }
                }

                string[] nativePanelFields =
                {
                    "NotificationPanel",
                    "TaskbarPanel",
                    "PureModePanel",
                    "UpgradePanel",
                    "PrivacyPanel",
                    "SubPageOverlay"
                };

                foreach (string name in nativePanelFields)
                {
                    if (t.GetField(name, flags)?.GetValue(settingsWindow) is FrameworkElement panel)
                    {
                        _nativePanels.Add(panel);
                        panel.IsVisibleChanged += OnNativePanelIsVisibleChanged;
                    }
                }

                AdaptiveSettingsStore.ModuleChanged += OnStoreModuleChanged;
                SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
                settingsWindow.IsVisibleChanged += OnWindowIsVisibleChanged;
                settingsWindow.Closed += OnWindowClosed;
            }

            private void OnNativeTabClicked(object sender, RoutedEventArgs e)
            {
                DeselectToolboxTab();
            }

            private void OnNativePanelIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
            {
                if (e.NewValue is true)
                {
                    DeselectToolboxTab();
                }
            }

            private void OnWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
            {
                if (_disposed || e.NewValue is not true)
                {
                    return;
                }

                SyncTogglesFromStore();

                if (_nativePanels.Any(p => p.Visibility == Visibility.Visible))
                {
                    DeselectToolboxTab();
                }
                else if (_isToolboxSelected)
                {
                    ApplyToolboxButtonVisualState(true);
                }
            }

            private void OnSystemPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
            {
                if (_disposed || e.Category != UserPreferenceCategory.General)
                {
                    return;
                }

                _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (!_disposed)
                    {
                        ApplyToolboxButtonVisualState(_isToolboxSelected);
                    }
                }));
            }

            private void OnStoreModuleChanged(string moduleId, bool enabled)
            {
                if (_disposed)
                {
                    return;
                }

                _window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_disposed)
                    {
                        return;
                    }

                    if (_toggleControls.TryGetValue(moduleId, out CheckBox? cb) && cb.IsChecked != enabled)
                    {
                        cb.IsChecked = enabled;
                    }
                }));
            }

            private void OnWindowClosed(object? sender, EventArgs e)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                AdaptiveSettingsStore.ModuleChanged -= OnStoreModuleChanged;
                SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
                _window.IsVisibleChanged -= OnWindowIsVisibleChanged;
                _window.Closed -= OnWindowClosed;

                foreach (var panel in _nativePanels)
                {
                    panel.IsVisibleChanged -= OnNativePanelIsVisibleChanged;
                }
            }

            private void SelectToolboxTab()
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    if (_showTabMethod != null)
                    {
                        _showTabMethod.Invoke(_window, new object[] { ToolboxTabIndex });
                    }
                    else
                    {
                        foreach (var p in _nativePanels)
                        {
                            p.Visibility = Visibility.Collapsed;
                        }
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warn($"ShowTab({ToolboxTabIndex}) failed: {ex.Message}");
                    foreach (var p in _nativePanels)
                    {
                        p.Visibility = Visibility.Collapsed;
                    }
                }

                _toolboxPanel.Visibility = Visibility.Visible;
                _toolboxPanel.ScrollToHome();
                _isToolboxSelected = true;
                ApplyToolboxButtonVisualState(true);
            }

            private void DeselectToolboxTab()
            {
                if (_disposed)
                {
                    return;
                }

                if (_toolboxPanel.Visibility != Visibility.Collapsed)
                {
                    _toolboxPanel.Visibility = Visibility.Collapsed;
                }

                if (_isToolboxSelected)
                {
                    _isToolboxSelected = false;
                    ApplyToolboxButtonVisualState(false);
                }
            }

            private void ApplyToolboxButtonVisualState(bool selected)
            {
                if (selected)
                {
                    Brush? selectedBg = _selectedTabBgField?.GetValue(_window) as Brush;
                    selectedBg ??= new SolidColorBrush(Color.FromArgb(51, 37, 111, 255));
                    _toolboxButton.Background = selectedBg;
                    _toolboxButtonText.SetResourceReference(TextBlock.ForegroundProperty, "SettingsSelectedTabTextBrush");
                }
                else
                {
                    _toolboxButton.Background = Brushes.Transparent;
                    _toolboxButtonText.SetResourceReference(TextBlock.ForegroundProperty, "SettingsMenuTextBrush");
                }

                _toolboxButton.BorderBrush = Brushes.Transparent;
                _toolboxButton.BorderThickness = new Thickness(0);
            }

            private static bool DetectChineseLanguage(Window settingsWindow, Type t, BindingFlags flags)
            {
                foreach (string field in new[] { "TitleTextBlock", "NotificationTabButton", "PrivacyTabButton" })
                {
                    object? val = t.GetField(field, flags)?.GetValue(settingsWindow);
                    string? txt = val switch
                    {
                        TextBlock tb => tb.Text,
                        Button btn => FindDescendantText(btn),
                        _ => null
                    };
                    if (!string.IsNullOrEmpty(txt) && txt.Any(c => c >= 0x4E00 && c <= 0x9FFF))
                    {
                        return true;
                    }
                }

                return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            }

            private static string? FindDescendantText(DependencyObject root)
            {
                if (root is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
                {
                    return tb.Text;
                }

                int count = VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < count; i++)
                {
                    string? childText = FindDescendantText(VisualTreeHelper.GetChild(root, i));
                    if (!string.IsNullOrEmpty(childText))
                    {
                        return childText;
                    }
                }
                return null;
            }

            private static T? FindDescendantByName<T>(DependencyObject root, string name) where T : FrameworkElement
            {
                int count = VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < count; i++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(root, i);
                    if (child is T fe && string.Equals(fe.Name, name, StringComparison.Ordinal))
                    {
                        return fe;
                    }

                    var nested = FindDescendantByName<T>(child, name);
                    if (nested != null)
                    {
                        return nested;
                    }
                }
                return null;
            }

            private static (Button Button, TextBlock Label) CreateSidebarTabButton(
                Button templateButton,
                FrameworkElement? templateIcon,
                bool isChinese)
            {
                string title = isChinese ? "Magic 视界工具箱" : "Magic Toolbox";

                var btn = new Button
                {
                    Name = ToolboxTabButtonName,
                    Width = templateButton.Width,
                    Height = templateButton.Height,
                    MinWidth = templateButton.MinWidth,
                    MinHeight = templateButton.MinHeight,
                    Margin = templateButton.Margin,
                    Padding = templateButton.Padding,
                    HorizontalAlignment = templateButton.HorizontalAlignment,
                    VerticalAlignment = templateButton.VerticalAlignment,
                    HorizontalContentAlignment = templateButton.HorizontalContentAlignment,
                    VerticalContentAlignment = templateButton.VerticalContentAlignment,
                    Style = templateButton.Style,
                    Cursor = templateButton.Cursor ?? Cursors.Hand,
                    Focusable = templateButton.Focusable,
                    Background = Brushes.Transparent,
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(0)
                };
                AutomationProperties.SetName(btn, title);

                Panel? origPanel = templateButton.Content as Panel;
                Panel contentPanel;
                if (origPanel is StackPanel origSp)
                {
                    contentPanel = new StackPanel
                    {
                        Orientation = origSp.Orientation,
                        Width = origSp.Width,
                        Height = origSp.Height,
                        Margin = origSp.Margin,
                        HorizontalAlignment = origSp.HorizontalAlignment,
                        VerticalAlignment = origSp.VerticalAlignment
                    };
                }
                else if (origPanel is Canvas origCv)
                {
                    contentPanel = new Canvas
                    {
                        Width = origCv.Width,
                        Height = origCv.Height,
                        Margin = origCv.Margin
                    };
                }
                else
                {
                    contentPanel = new Grid
                    {
                        Margin = origPanel?.Margin ?? new Thickness(0),
                        HorizontalAlignment = origPanel?.HorizontalAlignment ?? HorizontalAlignment.Stretch,
                        VerticalAlignment = origPanel?.VerticalAlignment ?? VerticalAlignment.Stretch
                    };
                    if (origPanel is Grid origGrid)
                    {
                        foreach (var cd in origGrid.ColumnDefinitions)
                        {
                            ((Grid)contentPanel).ColumnDefinitions.Add(new ColumnDefinition { Width = cd.Width });
                        }
                    }
                }

                UIElement? origIconChild = templateIcon;
                TextBlock? origTextChild = null;
                if (origPanel != null)
                {
                    origTextChild = origPanel.Children.OfType<TextBlock>().FirstOrDefault();
                    origIconChild ??= origPanel.Children.Cast<UIElement>().FirstOrDefault(el => el is not TextBlock);
                }

                FrameworkElement iconBadge = CreateToolboxBadgeIcon(origIconChild as FrameworkElement);
                var label = new TextBlock
                {
                    Text = title,
                    FontFamily = origTextChild?.FontFamily ?? templateButton.FontFamily ?? new FontFamily("HONOR Sans Design"),
                    FontSize = origTextChild?.FontSize > 0 ? origTextChild.FontSize : (templateButton.FontSize > 0 ? templateButton.FontSize : 14.0),
                    FontWeight = origTextChild?.FontWeight ?? FontWeights.Normal,
                    Margin = origTextChild?.Margin ?? (contentPanel is StackPanel ? new Thickness(10, 0, 0, 0) : new Thickness(0)),
                    VerticalAlignment = origTextChild?.VerticalAlignment ?? VerticalAlignment.Center,
                    HorizontalAlignment = origTextChild?.HorizontalAlignment ?? HorizontalAlignment.Left
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, "SettingsMenuTextBrush");

                if (contentPanel is Canvas)
                {
                    if (origIconChild != null)
                    {
                        double il = Canvas.GetLeft(origIconChild);
                        double it = Canvas.GetTop(origIconChild);
                        if (!double.IsNaN(il)) Canvas.SetLeft(iconBadge, il);
                        if (!double.IsNaN(it)) Canvas.SetTop(iconBadge, it);
                    }
                    else
                    {
                        Canvas.SetLeft(iconBadge, 12);
                        Canvas.SetTop(iconBadge, 12);
                    }

                    if (origTextChild != null)
                    {
                        double tl = Canvas.GetLeft(origTextChild);
                        double tt = Canvas.GetTop(origTextChild);
                        if (!double.IsNaN(tl)) Canvas.SetLeft(label, tl);
                        if (!double.IsNaN(tt)) Canvas.SetTop(label, tt);
                    }
                    else
                    {
                        Canvas.SetLeft(label, 38);
                        Canvas.SetTop(label, 10);
                    }
                }
                else if (contentPanel is Grid && origIconChild != null && origTextChild != null)
                {
                    Grid.SetColumn(iconBadge, Grid.GetColumn(origIconChild));
                    Grid.SetRow(iconBadge, Grid.GetRow(origIconChild));
                    Grid.SetColumn(label, Grid.GetColumn(origTextChild));
                    Grid.SetRow(label, Grid.GetRow(origTextChild));
                }

                contentPanel.Children.Add(iconBadge);
                contentPanel.Children.Add(label);
                btn.Content = contentPanel;

                return (btn, label);
            }

            private static FrameworkElement CreateToolboxBadgeIcon(FrameworkElement? templateIcon)
            {
                double size = templateIcon != null && templateIcon.Width > 0 && templateIcon.Width <= 32
                    ? templateIcon.Width
                    : 16.0;
                double height = templateIcon != null && templateIcon.Height > 0 && templateIcon.Height <= 32
                    ? templateIcon.Height
                    : size;

                var badgeGradient = new LinearGradientBrush(
                    Color.FromRgb(0x74, 0x62, 0xFF),
                    Color.FromRgb(0x46, 0x56, 0xE6),
                    new Point(0, 0),
                    new Point(1, 1));
                badgeGradient.Freeze();

                var glyph = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse(
                        "M5.6,3.2 C5.6,2.87 5.87,2.6 6.2,2.6 L9.8,2.6 C10.13,2.6 10.4,2.87 10.4,3.2 L10.4,4.2 L12.3,4.2 C12.96,4.2 13.5,4.74 13.5,5.4 L13.5,7.3 L9.4,7.3 L9.4,6.7 C9.4,6.48 9.22,6.3 9.0,6.3 L7.0,6.3 C6.78,6.3 6.6,6.48 6.6,6.7 L6.6,7.3 L2.5,7.3 L2.5,5.4 C2.5,4.74 3.04,4.2 3.7,4.2 L5.6,4.2 Z M6.7,3.6 L6.7,4.2 L9.3,4.2 L9.3,3.6 Z M2.5,8.2 L6.6,8.2 L6.6,8.7 C6.6,9.03 6.87,9.3 7.2,9.3 L8.8,9.3 C9.13,9.3 9.4,9.03 9.4,8.7 L9.4,8.2 L13.5,8.2 L13.5,11.6 C13.5,12.26 12.96,12.8 12.3,12.8 L3.7,12.8 C3.04,12.8 2.5,12.26 2.5,11.6 Z"),
                    Fill = Brushes.White,
                    Stretch = Stretch.Uniform
                };

                var viewbox = new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    Child = glyph,
                    Margin = new Thickness(2.2)
                };

                return new Border
                {
                    Width = size,
                    Height = height,
                    CornerRadius = new CornerRadius(Math.Round(size * 0.285, 2)),
                    Background = badgeGradient,
                    Margin = templateIcon?.Margin ?? new Thickness(0),
                    VerticalAlignment = templateIcon?.VerticalAlignment ?? VerticalAlignment.Center,
                    HorizontalAlignment = templateIcon?.HorizontalAlignment ?? HorizontalAlignment.Left,
                    SnapsToDevicePixels = true,
                    Child = viewbox
                };
            }

            private static bool InsertSidebarButton(Button privacyButton, Button? upgradeButton, Button toolboxButton)
            {
                DependencyObject? parent = VisualTreeHelper.GetParent(privacyButton) ?? LogicalTreeHelper.GetParent(privacyButton);
                if (parent == null)
                {
                    return false;
                }

                if (parent is StackPanel stack)
                {
                    int idx = stack.Children.IndexOf(privacyButton);
                    if (idx >= 0)
                    {
                        stack.Children.Insert(idx + 1, toolboxButton);
                    }
                    else
                    {
                        stack.Children.Add(toolboxButton);
                    }
                    return true;
                }

                if (parent is Canvas canvas)
                {
                    double pTop = Canvas.GetTop(privacyButton);
                    double pLeft = Canvas.GetLeft(privacyButton);
                    double step = 44.0;
                    if (upgradeButton != null && !double.IsNaN(Canvas.GetTop(upgradeButton)) && !double.IsNaN(pTop))
                    {
                        double delta = pTop - Canvas.GetTop(upgradeButton);
                        if (delta >= 30.0 && delta <= 72.0)
                        {
                            step = delta;
                        }
                    }

                    if (!double.IsNaN(pLeft)) Canvas.SetLeft(toolboxButton, pLeft);
                    Canvas.SetTop(toolboxButton, (double.IsNaN(pTop) ? 240.0 : pTop) + step);
                    canvas.Children.Add(toolboxButton);
                    return true;
                }

                if (parent is Grid grid)
                {
                    int pRow = Grid.GetRow(privacyButton);
                    int pCol = Grid.GetColumn(privacyButton);
                    bool sameRowStack = upgradeButton != null && Grid.GetRow(upgradeButton) == pRow;

                    if (sameRowStack || grid.RowDefinitions.Count == 0)
                    {
                        double step = 44.0;
                        if (upgradeButton != null)
                        {
                            double delta = privacyButton.Margin.Top - upgradeButton.Margin.Top;
                            if (delta >= 30.0 && delta <= 72.0)
                            {
                                step = delta;
                            }
                        }

                        var m = privacyButton.Margin;
                        toolboxButton.Margin = new Thickness(m.Left, m.Top + step, m.Right, m.Bottom);
                        Grid.SetRow(toolboxButton, pRow);
                        Grid.SetColumn(toolboxButton, pCol);
                        grid.Children.Add(toolboxButton);
                        return true;
                    }

                    int insertRow = pRow + 1;
                    GridLength rowHeight = pRow < grid.RowDefinitions.Count
                        ? grid.RowDefinitions[pRow].Height
                        : GridLength.Auto;

                    foreach (UIElement child in grid.Children)
                    {
                        int r = Grid.GetRow(child);
                        if (r >= insertRow)
                        {
                            Grid.SetRow(child, r + 1);
                        }
                    }

                    grid.RowDefinitions.Insert(Math.Min(insertRow, grid.RowDefinitions.Count), new RowDefinition { Height = rowHeight });
                    Grid.SetRow(toolboxButton, insertRow);
                    Grid.SetColumn(toolboxButton, pCol);
                    grid.Children.Add(toolboxButton);
                    return true;
                }

                if (parent is Panel genericPanel)
                {
                    genericPanel.Children.Add(toolboxButton);
                    return true;
                }

                return false;
            }

            private static (ScrollViewer Panel, Dictionary<string, CheckBox> Toggles) BuildToolboxScrollPanel(
                Window settingsWindow,
                FrameworkElement anchorPanel,
                Canvas? notificationCanvas,
                CheckBox? templateCheckBox,
                bool isChinese)
            {
                var toggles = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
                FontFamily baseFont = FindFontFamily(notificationCanvas) ?? new FontFamily("HONOR Sans Design");

                var scroll = new ScrollViewer
                {
                    Name = ToolboxPanelName,
                    Visibility = Visibility.Collapsed,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    PanningMode = PanningMode.VerticalOnly,
                    Focusable = false,
                    Margin = anchorPanel.Margin,
                    HorizontalAlignment = anchorPanel.HorizontalAlignment,
                    VerticalAlignment = anchorPanel.VerticalAlignment
                };

                if (anchorPanel is Control anchorCtrl)
                {
                    scroll.Padding = anchorCtrl.Padding;
                }

                if (VisualTreeHelper.GetParent(anchorPanel) is Canvas)
                {
                    scroll.Width = !double.IsNaN(anchorPanel.Width) && anchorPanel.Width > 0 ? anchorPanel.Width : 600.0;
                    scroll.Height = !double.IsNaN(anchorPanel.Height) && anchorPanel.Height > 0 ? anchorPanel.Height : 540.0;
                    double left = Canvas.GetLeft(anchorPanel);
                    double top = Canvas.GetTop(anchorPanel);
                    if (!double.IsNaN(left)) Canvas.SetLeft(scroll, left);
                    if (!double.IsNaN(top)) Canvas.SetTop(scroll, top);
                }

                double contentLeftMargin = anchorPanel.Margin.Left >= 20.0 ? 0.0 : 36.0;
                double contentRightMargin = anchorPanel.Margin.Right >= 20.0 ? 12.0 : 36.0;

                var rootStack = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Margin = new Thickness(contentLeftMargin, 15.0, contentRightMargin, 28.0)
                };

                // Page header matching Honor settings sections
                var pageTitle = new TextBlock
                {
                    Text = isChinese ? "Magic 视界工具箱" : "Magic Toolbox",
                    FontFamily = baseFont,
                    FontSize = 16.0,
                    FontWeight = FontWeights.Medium,
                    Margin = new Thickness(16.0, 0.0, 0.0, 4.0)
                };
                pageTitle.SetResourceReference(TextBlock.ForegroundProperty, "SettingsPrimaryTextBrush");
                rootStack.Children.Add(pageTitle);

                var pageSubtitle = new TextBlock
                {
                    Text = isChinese
                        ? $"功能配置与修复选项 · {ToolboxModuleRegistry.GitHubRepoUrl}"
                        : $"Configuration & fixes · {ToolboxModuleRegistry.GitHubRepoUrl}",
                    FontFamily = baseFont,
                    FontSize = 12.0,
                    TextWrapping = TextWrapping.Wrap,
                    Cursor = Cursors.Hand,
                    ToolTip = ToolboxModuleRegistry.GitHubRepoUrl,
                    Margin = new Thickness(16.0, 0.0, 0.0, 14.0)
                };
                pageSubtitle.SetResourceReference(TextBlock.ForegroundProperty, "SettingsSecondaryTextBrush");
                pageSubtitle.MouseLeftButtonUp += (_, _) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = ToolboxModuleRegistry.GitHubRepoUrl,
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                    }
                };
                rootStack.Children.Add(pageSubtitle);

                foreach (var cat in ToolboxModuleRegistry.Categories)
                {
                    var items = ToolboxModuleRegistry.Modules.Where(m => m.Category == cat.Category).ToList();
                    if (items.Count == 0)
                    {
                        continue;
                    }

                    var sectionHeader = new TextBlock
                    {
                        Text = isChinese ? cat.TitleZh : cat.TitleEn,
                        FontFamily = baseFont,
                        FontSize = 14.0,
                        FontWeight = FontWeights.Normal,
                        Margin = new Thickness(16.0, 6.0, 0.0, 8.0)
                    };
                    sectionHeader.SetResourceReference(TextBlock.ForegroundProperty, "SettingsPrimaryTextBrush");
                    rootStack.Children.Add(sectionHeader);

                    var cardBorder = new Border
                    {
                        CornerRadius = new CornerRadius(12),
                        BorderThickness = new Thickness(1),
                        Margin = new Thickness(0.0, 0.0, 0.0, 12.0),
                        SnapsToDevicePixels = true
                    };
                    cardBorder.SetResourceReference(Border.BackgroundProperty, "SettingsCardBackgroundBrush");
                    cardBorder.SetResourceReference(Border.BorderBrushProperty, "SettingsCardBorderBrush");

                    var cardStack = new StackPanel { Orientation = Orientation.Vertical };
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (i > 0)
                        {
                            var divider = new Border
                            {
                                Height = 1.0,
                                Margin = new Thickness(16.0, 0.0, 16.0, 0.0),
                                SnapsToDevicePixels = true
                            };
                            divider.SetResourceReference(Border.BackgroundProperty, "SettingsDividerBrush");
                            cardStack.Children.Add(divider);
                        }

                        var row = BuildModuleRow(settingsWindow, items[i], templateCheckBox, baseFont, isChinese, toggles);
                        cardStack.Children.Add(row);
                    }

                    cardBorder.Child = cardStack;
                    rootStack.Children.Add(cardBorder);
                }

                scroll.Content = rootStack;
                return (scroll, toggles);
            }

            private static FrameworkElement BuildModuleRow(
                Window settingsWindow,
                ToolboxModuleDescriptor module,
                CheckBox? templateCheckBox,
                FontFamily baseFont,
                bool isChinese,
                Dictionary<string, CheckBox> toggles)
            {
                var grid = new Grid
                {
                    Margin = new Thickness(16.0, 12.0, 16.0, 12.0),
                    MinHeight = 36.0
                };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var textPanel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0.0, 0.0, 16.0, 0.0)
                };

                string titleText = isChinese ? module.TitleZh : module.TitleEn;
                string descText = isChinese ? module.DescriptionZh : module.DescriptionEn;

                var titleTb = new TextBlock
                {
                    Text = titleText,
                    FontFamily = baseFont,
                    FontSize = 14.0,
                    FontWeight = FontWeights.Normal,
                    TextWrapping = TextWrapping.Wrap
                };
                titleTb.SetResourceReference(TextBlock.ForegroundProperty, "SettingsPrimaryTextBrush");
                textPanel.Children.Add(titleTb);

                if (!string.IsNullOrEmpty(descText))
                {
                    var descTb = new TextBlock
                    {
                        Text = descText,
                        FontFamily = baseFont,
                        FontSize = 12.0,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0.0, 3.0, 0.0, 0.0)
                    };
                    descTb.SetResourceReference(TextBlock.ForegroundProperty, "SettingsSecondaryTextBrush");
                    textPanel.Children.Add(descTb);
                }

                Grid.SetColumn(textPanel, 0);
                grid.Children.Add(textPanel);

                FrameworkElement actionControl;
                if (module.ControlKind == ToolboxControlKind.Toggle)
                {
                    bool enabled = AdaptiveSettingsStore.IsModuleEnabled(module.Id, module.DefaultEnabled);
                    var cb = new CheckBox
                    {
                        Name = "ToolboxCheck_" + module.Id,
                        Width = templateCheckBox != null && templateCheckBox.Width > 0 ? templateCheckBox.Width : 44.0,
                        Height = templateCheckBox != null && templateCheckBox.Height > 0 ? templateCheckBox.Height : 24.0,
                        Style = templateCheckBox?.Style,
                        Cursor = templateCheckBox?.Cursor ?? Cursors.Hand,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsChecked = enabled,
                        ToolTip = descText
                    };
                    AutomationProperties.SetName(cb, titleText);

                    string moduleId = module.Id;
                    cb.Click += (_, _) =>
                    {
                        AdaptiveSettingsStore.SetModuleEnabled(moduleId, cb.IsChecked == true);
                    };

                    toggles[module.Id] = cb;
                    actionControl = cb;
                }
                else
                {
                    string actionText = isChinese
                        ? (module.ActionTextZh ?? "执行")
                        : (module.ActionTextEn ?? "Run");
                    actionControl = CreateActionButton(settingsWindow, actionText, baseFont, module.ActionHandler, module.IsDestructiveAction);
                }

                Grid.SetColumn(actionControl, 1);
                grid.Children.Add(actionControl);
                return grid;
            }

            private static Button CreateActionButton(Window settingsWindow, string text, FontFamily baseFont, Action? handler, bool isDestructive = false)
            {
                var template = new ControlTemplate(typeof(Button));
                var borderFactory = new FrameworkElementFactory(typeof(Border));
                borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
                borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
                borderFactory.SetResourceReference(Border.BackgroundProperty, "SettingsSubCardBackgroundBrush");
                if (isDestructive)
                {
                    borderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x66, 0xE5, 0x48, 0x4D)));
                }
                else
                {
                    borderFactory.SetResourceReference(Border.BorderBrushProperty, "SettingsCardBorderBrush");
                }
                borderFactory.SetValue(Border.PaddingProperty, new Thickness(12, 5, 12, 5));

                var presenterFactory = new FrameworkElementFactory(typeof(ContentPresenter));
                presenterFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                presenterFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                borderFactory.AppendChild(presenterFactory);
                template.VisualTree = borderFactory;

                var btn = new Button
                {
                    Content = text,
                    FontFamily = baseFont,
                    FontSize = 12.5,
                    MinWidth = 76.0,
                    Height = 28.0,
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                    Template = template,
                    Focusable = false
                };
                if (isDestructive)
                {
                    btn.Foreground = new SolidColorBrush(Color.FromRgb(0xEB, 0x57, 0x57));
                }
                else
                {
                    btn.SetResourceReference(Control.ForegroundProperty, "SettingsPrimaryTextBrush");
                }
                AutomationProperties.SetName(btn, text);

                if (handler != null)
                {
                    btn.Click += (_, _) => handler();
                }

                return btn;
            }

            private static FontFamily? FindFontFamily(Canvas? notificationCanvas)
            {
                if (notificationCanvas == null)
                {
                    return null;
                }

                var tb = notificationCanvas.Children.OfType<TextBlock>().FirstOrDefault();
                if (tb?.FontFamily != null)
                {
                    return tb.FontFamily;
                }

                foreach (var border in notificationCanvas.Children.OfType<Border>())
                {
                    if (border.Child is Panel inner)
                    {
                        var childTb = inner.Children.OfType<TextBlock>().FirstOrDefault();
                        if (childTb?.FontFamily != null)
                        {
                            return childTb.FontFamily;
                        }
                    }
                }
                return null;
            }

            private static bool InsertContentPanel(FrameworkElement anchorPanel, ScrollViewer toolboxPanel)
            {
                DependencyObject? parent = VisualTreeHelper.GetParent(anchorPanel) ?? LogicalTreeHelper.GetParent(anchorPanel);
                if (parent is not Panel hostPanel)
                {
                    return false;
                }

                if (hostPanel is Grid)
                {
                    Grid.SetRow(toolboxPanel, Grid.GetRow(anchorPanel));
                    Grid.SetColumn(toolboxPanel, Grid.GetColumn(anchorPanel));
                    Grid.SetRowSpan(toolboxPanel, Grid.GetRowSpan(anchorPanel));
                    Grid.SetColumnSpan(toolboxPanel, Grid.GetColumnSpan(anchorPanel));
                }

                Panel.SetZIndex(toolboxPanel, Panel.GetZIndex(anchorPanel));

                int idx = hostPanel.Children.IndexOf(anchorPanel);
                if (idx >= 0)
                {
                    hostPanel.Children.Insert(idx + 1, toolboxPanel);
                }
                else
                {
                    hostPanel.Children.Add(toolboxPanel);
                }
                return true;
            }
        }
    }
}

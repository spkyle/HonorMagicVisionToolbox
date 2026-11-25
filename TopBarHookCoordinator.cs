using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace TopBarAdaptiveColor
{
    internal static class TopBarHookCoordinator
    {
        private const string TopBarWindowTypeName = "MagicTaskbarWpf.TopBarWindow";
        private const string TopBarSettingsWindowTypeName = "MagicTaskbarWpf.TopBarSettingsWindow";
        private const string TopBarAcrylicPopupWindowTypeName = "MagicTaskbarWpf.TopBarAcrylicPopupWindow";
        private const string DockPreviewWindowTypeName = "MagicTaskbarWpf.DockPreviewWindow";
        private const string DockMainWindowTypeName = "MagicTaskbarWpf.MainWindow";

        private static readonly ConditionalWeakTable<Window, AdaptiveTopBarController> Controllers = new();
        private static readonly ConditionalWeakTable<Window, HiddenTrayPinEnhancer> HiddenTrayPinEnhancers = new();
        private static readonly ConditionalWeakTable<Window, DockPreviewEnhancer> DockPreviewEnhancers = new();
        private static readonly ConditionalWeakTable<Window, DockStartRightClickEnhancer> DockStartEnhancers = new();
        private static readonly ConditionalWeakTable<Window, DockHighRefreshRateEnhancer> DockHighRefreshEnhancers = new();
        private static Timer? _uiPollTimer;
        private static int _uiHooked;

        public static void ScheduleAttach()
        {
            _uiPollTimer = new Timer(_ => TryHookApplication(), null, 50, 100);
        }

        private static void TryHookApplication()
        {
            if (Volatile.Read(ref _uiHooked) == 1)
            {
                return;
            }

            try
            {
                Application? app = Application.Current;
                Dispatcher? dispatcher = app?.Dispatcher;
                if (app == null || dispatcher == null || dispatcher.HasShutdownStarted)
                {
                    return;
                }

                if (Interlocked.Exchange(ref _uiHooked, 1) == 1)
                {
                    return;
                }

                _uiPollTimer?.Dispose();
                _uiPollTimer = null;

                dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(AttachOnUiThread));
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"TryHookApplication error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void AttachOnUiThread()
        {
            try
            {
                EventManager.RegisterClassHandler(
                    typeof(Window),
                    FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnAnyWindowLoaded),
                    handledEventsToo: true);

                Application? app = Application.Current;
                NativeDesktopController.Initialize();
                MagicComponentController.Initialize(app);
                DesktopRightClickAccelerator.Initialize(app?.Dispatcher);
                if (app != null)
                {
                    var snapshot = new System.Collections.Generic.List<Window>();
                    foreach (Window w in app.Windows)
                    {
                        snapshot.Add(w);
                    }
                    foreach (Window w in snapshot)
                    {
                        TryAttachWindow(w);
                    }
                }

                DiagnosticLog.Info("TopBarHookCoordinator registered Window.Loaded handler.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"AttachOnUiThread failed: {ex}");
            }
        }

        private static void OnAnyWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is Window w)
            {
                TryAttachWindow(w);
            }
        }

        private static void TryAttachWindow(Window w)
        {
            if (MagicComponentController.ShouldSuppressWindow(w))
            {
                return;
            }

            string? fullName = w.GetType().FullName;
            if (string.Equals(fullName, TopBarWindowTypeName, StringComparison.Ordinal))
            {
                if (!Controllers.TryGetValue(w, out _))
                {
                    var controller = new AdaptiveTopBarController(w);
                    Controllers.Add(w, controller);
                }
                if (!HiddenTrayPinEnhancers.TryGetValue(w, out _))
                {
                    HiddenTrayPinEnhancers.Add(w, new HiddenTrayPinEnhancer(w));
                }
                return;
            }

            if (string.Equals(fullName, TopBarSettingsWindowTypeName, StringComparison.Ordinal))
            {
                TopBarSettingsInjector.Attach(w);
                TopBarSettingsFastReuse.Attach(w);
                return;
            }

            if (string.Equals(fullName, TopBarAcrylicPopupWindowTypeName, StringComparison.Ordinal))
            {
                TopBarAcrylicPopupClamper.Attach(w);
                return;
            }

            if (string.Equals(fullName, DockPreviewWindowTypeName, StringComparison.Ordinal))
            {
                if (!DockPreviewEnhancers.TryGetValue(w, out _))
                {
                    DockPreviewEnhancers.Add(w, new DockPreviewEnhancer(w));
                }
                return;
            }

            if (string.Equals(fullName, DockMainWindowTypeName, StringComparison.Ordinal))
            {
                if (!DockStartEnhancers.TryGetValue(w, out _))
                {
                    DockStartEnhancers.Add(w, new DockStartRightClickEnhancer(w));
                }
                if (!DockHighRefreshEnhancers.TryGetValue(w, out _))
                {
                    DockHighRefreshEnhancers.Add(w, new DockHighRefreshRateEnhancer(w));
                }
            }
        }
    }
}

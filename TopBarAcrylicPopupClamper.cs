using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TopBarAdaptiveColor
{
    /// <summary>
    /// Ensures TopBarAcrylicPopupWindow (Battery, Volume, Network, Bluetooth, InputMethod, ContextMenu)
    /// stays inside its monitor's safe work area even when hiding buttons like YOYO Claw shifts anchors right.
    /// </summary>
    internal sealed class TopBarAcrylicPopupClamper
    {
        private const double EdgePaddingDip = 6.0;
        private const uint MonitorDefaultToNearest = 0x00000002;

        private static readonly ConditionalWeakTable<Window, TopBarAcrylicPopupClamper> Attached = new();

        private readonly Window _popup;
        private readonly MethodInfo? _syncBackdropMethod;
        private bool _isClamping;
        private bool _renderClampQueued;

        private TopBarAcrylicPopupClamper(Window popup)
        {
            _popup = popup;
            _syncBackdropMethod = popup.GetType().GetMethod(
                "SyncBackdrop",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            _popup.IsVisibleChanged += OnPopupVisibleChanged;
            _popup.LocationChanged += OnPopupGeometryChanged;
            _popup.SizeChanged += OnPopupGeometryChanged;
            _popup.Closed += OnPopupClosed;

            ClampIfVisible();
        }

        public static void Attach(Window popup)
        {
            if (popup == null)
            {
                return;
            }

            Attached.GetValue(popup, w => new TopBarAcrylicPopupClamper(w));
        }

        private void OnPopupVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is true)
            {
                ClampNow(syncBackdrop: true);
                ScheduleRenderClamp();
            }
        }

        private void OnPopupGeometryChanged(object? sender, EventArgs e)
        {
            if (_popup.IsVisible)
            {
                ClampNow(syncBackdrop: true);
            }
        }

        private void ScheduleRenderClamp()
        {
            if (_renderClampQueued || _popup.Dispatcher.HasShutdownStarted)
            {
                return;
            }

            _renderClampQueued = true;
            _popup.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                _renderClampQueued = false;
                if (_popup.IsVisible)
                {
                    ClampNow(syncBackdrop: true);
                }
            }));
        }

        private void ClampIfVisible()
        {
            if (_popup.IsVisible)
            {
                ClampNow(syncBackdrop: true);
            }
        }

        internal bool ClampNow(bool syncBackdrop)
        {
            if (_isClamping || !AdaptiveSettingsStore.IsTopBarPopupEdgeClampEnabled)
            {
                return false;
            }

            double left = _popup.Left;
            double top = _popup.Top;
            if (double.IsNaN(left) || double.IsInfinity(left) || double.IsNaN(top) || double.IsInfinity(top))
            {
                return false;
            }

            double width = ResolveExtent(_popup.ActualWidth, _popup.Width, _popup.DesiredSize.Width);
            double height = ResolveExtent(_popup.ActualHeight, _popup.Height, _popup.DesiredSize.Height);
            if (width <= 1.0 || height <= 1.0)
            {
                return false;
            }

            Rect workDip = GetMonitorWorkAreaDip(_popup, left + 8.0, top + 8.0);
            if (workDip.IsEmpty || workDip.Width <= 10.0 || workDip.Height <= 10.0)
            {
                return false;
            }

            double minLeft = workDip.Left + EdgePaddingDip;
            double maxLeft = workDip.Right - width - EdgePaddingDip;
            double targetLeft = maxLeft >= minLeft
                ? Math.Clamp(left, minLeft, maxLeft)
                : minLeft;

            double minTop = workDip.Top + 2.0;
            double maxTop = workDip.Bottom - height - EdgePaddingDip;
            double targetTop = maxTop >= minTop
                ? Math.Clamp(top, minTop, maxTop)
                : minTop;

            if (Math.Abs(targetLeft - left) < 0.5 && Math.Abs(targetTop - top) < 0.5)
            {
                return false;
            }

            _isClamping = true;
            try
            {
                _popup.Left = targetLeft;
                _popup.Top = targetTop;

                if (syncBackdrop && _popup.IsVisible)
                {
                    _syncBackdropMethod?.Invoke(_popup, null);
                }

                DiagnosticLog.Info(
                    $"Clamped TopBarAcrylicPopupWindow: ({left:F1},{top:F1} {width:F0}x{height:F0}) -> ({targetLeft:F1},{targetTop:F1}) within work=({workDip.Left:F0},{workDip.Top:F0},{workDip.Right:F0},{workDip.Bottom:F0}).");
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"TopBarAcrylicPopupClamper failed: {ex.Message}");
                return false;
            }
            finally
            {
                _isClamping = false;
            }
        }

        private static double ResolveExtent(double actual, double explicitSize, double desired)
        {
            if (actual > 1.0 && double.IsFinite(actual))
            {
                return actual;
            }
            if (!double.IsNaN(explicitSize) && explicitSize > 1.0 && double.IsFinite(explicitSize))
            {
                return explicitSize;
            }
            if (desired > 1.0 && double.IsFinite(desired))
            {
                return desired;
            }
            return 0.0;
        }

        private static Rect GetMonitorWorkAreaDip(Window popup, double probeX, double probeY)
        {
            Matrix toDevice = Matrix.Identity;
            Matrix fromDevice = Matrix.Identity;

            Visual? refVisual = PresentationSource.FromVisual(popup) != null ? popup : popup.Owner;
            if (refVisual != null && PresentationSource.FromVisual(refVisual) is HwndSource hs && hs.CompositionTarget != null)
            {
                toDevice = hs.CompositionTarget.TransformToDevice;
                fromDevice = hs.CompositionTarget.TransformFromDevice;
            }

            Point devPt = toDevice.Transform(new Point(probeX, probeY));
            IntPtr hMon = MonitorFromPoint(
                new POINT { X = (int)Math.Round(devPt.X), Y = (int)Math.Round(devPt.Y) },
                MonitorDefaultToNearest);

            if (hMon != IntPtr.Zero)
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMon, ref mi))
                {
                    RECT r = (mi.rcWork.Right > mi.rcWork.Left && mi.rcWork.Bottom > mi.rcWork.Top)
                        ? mi.rcWork
                        : mi.rcMonitor;

                    Point tl = fromDevice.Transform(new Point(r.Left, r.Top));
                    Point br = fromDevice.Transform(new Point(r.Right, r.Bottom));
                    return new Rect(tl, br);
                }
            }

            return new Rect(
                SystemParameters.WorkArea.Left,
                SystemParameters.WorkArea.Top,
                SystemParameters.WorkArea.Width,
                SystemParameters.WorkArea.Height);
        }

        private void OnPopupClosed(object? sender, EventArgs e)
        {
            _popup.IsVisibleChanged -= OnPopupVisibleChanged;
            _popup.LocationChanged -= OnPopupGeometryChanged;
            _popup.SizeChanged -= OnPopupGeometryChanged;
            _popup.Closed -= OnPopupClosed;
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
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    }
}

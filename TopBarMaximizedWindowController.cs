using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TopBarAdaptiveColor
{
    /// <summary>
    /// Allows users to restore & drag the underlying maximized window by grabbing anywhere in the
    /// blank TopBar area (threshold >= 6 DIP in any direction), or double-clicking the blank area
    /// to toggle restore/re-maximize without adding any visible chrome.
    /// </summary>
    internal sealed class TopBarMaximizedWindowController : IDisposable
    {
        internal const double DragThresholdDip = 6.0;

        private const int MonitorDefaultToNearest = 2;
        private const int GwlStyle = -16;

        private const long WsChild = 0x40000000L;
        private const long WsDisabled = 0x08000000L;
        private const long WsThickFrame = 0x00040000L;
        private const long WsMaximizeBox = 0x00010000L;

        private const uint WmSysCommand = 0x0112;
        private const int ScMaximize = 0xF030;
        private const int ScRestore = 0xF120;

        private const int SwShowNormal = 1;
        private const int SwRestore = 9;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpAsyncWindowPos = 0x4000;
        private const int VkLButton = 0x01;

        private readonly Window _window;
        private readonly PropertyInfo? _isAnyPopupOpenProp;
        private readonly DispatcherTimer _fallbackDragTimer;

        private bool _isArmed;
        private IntPtr _armedTargetHwnd;
        private Point _pressPointDip;

        private IntPtr _lastRestoredHwnd;
        private long _lastRestoredTick;

        private IntPtr _fallbackDragHwnd;
        private POINT _fallbackStartCursorPx;
        private POINT _lastDragCursorPx;
        private int _fallbackStartLeftPx;
        private int _fallbackStartTopPx;
        private RECT _fallbackMonitorRect;
        private RECT _fallbackWorkRect;
        private bool _fallbackDragActive;
        private bool _disposed;

        public TopBarMaximizedWindowController(Window topBarWindow)
        {
            _window = topBarWindow ?? throw new ArgumentNullException(nameof(topBarWindow));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _isAnyPopupOpenProp = topBarWindow.GetType().GetProperty("IsAnyTopBarPopupOpen", flags);

            _fallbackDragTimer = new DispatcherTimer(DispatcherPriority.Send, _window.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(8)
            };
            _fallbackDragTimer.Tick += OnFallbackDragTimerTick;

            _window.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            _window.PreviewMouseMove += OnPreviewMouseMove;
            _window.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
            _window.LostMouseCapture += OnLostMouseCapture;
            AdaptiveSettingsStore.TopBarMaximizedWindowDragChanged += OnSettingChanged;

            DiagnosticLog.Info($"TopBarMaximizedWindowController attached (enabled={AdaptiveSettingsStore.IsTopBarMaximizedWindowDragEnabled}).");
        }

        private void OnSettingChanged(bool enabled)
        {
            if (_disposed || enabled)
            {
                return;
            }

            _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                CancelDragArm();
                StopFallbackDrag(snapMaximizeIfAtTop: false);
            }));
        }

        internal bool IsBlankTopBarHit(DependencyObject? source)
        {
            if (source == null || _disposed || !AdaptiveSettingsStore.IsTopBarMaximizedWindowDragEnabled)
            {
                return false;
            }

            if (_isAnyPopupOpenProp?.GetValue(_window) is true)
            {
                return false;
            }

            DependencyObject? current = source;
            while (current != null && !ReferenceEquals(current, _window))
            {
                if (current is ButtonBase ||
                    current is MenuItem ||
                    current is MenuBase ||
                    current is Selector ||
                    current is TextBoxBase ||
                    current is RangeBase ||
                    current is Thumb ||
                    current is ScrollBar ||
                    current is Popup)
                {
                    return false;
                }

                if (current is FrameworkElement fe)
                {
                    string name = fe.Name;
                    if (!string.IsNullOrEmpty(name) &&
                        (name.EndsWith("PopupRoot", StringComparison.Ordinal) ||
                         name.EndsWith("PopupBorder", StringComparison.Ordinal) ||
                         string.Equals(name, "NetworkSpeedContainer", StringComparison.Ordinal)))
                    {
                        return false;
                    }
                }

                DependencyObject? parent = VisualTreeHelper.GetParent(current);
                parent ??= LogicalTreeHelper.GetParent(current);
                current = parent;
            }

            return true;
        }

        internal static RECT ComputeAnchoredRestoreRect(
            RECT existingNormal,
            RECT monitorRect,
            RECT workRect,
            POINT cursorPx,
            double dpiScale)
        {
            int monWidth = Math.Max(400, monitorRect.Right - monitorRect.Left);
            int workWidth = Math.Max(400, workRect.Right - workRect.Left);
            int workHeight = Math.Max(300, workRect.Bottom - workRect.Top);

            int normalW = existingNormal.Right - existingNormal.Left;
            int normalH = existingNormal.Bottom - existingNormal.Top;

            bool invalidOrNearlyMaximized =
                normalW < 320 || normalH < 240 ||
                (normalW >= (int)(workWidth * 0.98) && normalH >= (int)(workHeight * 0.96));

            if (invalidOrNearlyMaximized)
            {
                normalW = Math.Clamp((int)Math.Round(workWidth * 0.72), 640, Math.Max(640, workWidth - 60));
                normalH = Math.Clamp((int)Math.Round(workHeight * 0.78), 480, Math.Max(480, workHeight - 48));
            }
            else
            {
                normalW = Math.Clamp(normalW, 360, workWidth);
                normalH = Math.Clamp(normalH, 260, workHeight);
            }

            double ratioX = Math.Clamp((double)(cursorPx.X - monitorRect.Left) / monWidth, 0.08, 0.92);
            int left = (int)Math.Round(cursorPx.X - normalW * ratioX);

            // Keep cursor inside the restored caption horizontal span with margin on both ends.
            int minLeftForCursor = cursorPx.X - normalW + 48;
            int maxLeftForCursor = cursorPx.X - 48;
            left = Math.Clamp(left, minLeftForCursor, maxLeftForCursor);

            int minScreenLeft = workRect.Left - Math.Max(0, normalW / 4);
            int maxScreenLeft = workRect.Right - Math.Min(normalW, Math.Max(160, (normalW * 3) / 4));
            if (minScreenLeft <= maxScreenLeft)
            {
                left = Math.Clamp(left, minScreenLeft, maxScreenLeft);
            }

            int captionOffset = Math.Max(8, (int)Math.Round(14.0 * Math.Max(1.0, dpiScale)));
            int top = Math.Max(monitorRect.Top + 2, cursorPx.Y - captionOffset);

            return new RECT
            {
                Left = left,
                Top = top,
                Right = left + normalW,
                Bottom = top + normalH
            };
        }

        private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_disposed ||
                e.ChangedButton != MouseButton.Left ||
                !AdaptiveSettingsStore.IsTopBarMaximizedWindowDragEnabled ||
                _fallbackDragActive)
            {
                CancelDragArm();
                return;
            }

            if (!IsBlankTopBarHit(e.OriginalSource as DependencyObject))
            {
                CancelDragArm();
                return;
            }

            if (e.ClickCount >= 2)
            {
                CancelDragArm();
                if (TryHandleBlankDoubleClick())
                {
                    e.Handled = true;
                }
                return;
            }

            IntPtr topBarHwnd = new WindowInteropHelper(_window).Handle;
            if (!TryFindTopMaximizedTarget(topBarHwnd, out IntPtr targetHwnd, out _))
            {
                CancelDragArm();
                return;
            }

            _isArmed = true;
            _armedTargetHwnd = targetHwnd;
            _pressPointDip = e.GetPosition(_window);

            // Capture temporarily so downward drags leaving the 32 DIP strip still reach the 6px threshold.
            _window.CaptureMouse();
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_disposed || !_isArmed)
            {
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed ||
                !AdaptiveSettingsStore.IsTopBarMaximizedWindowDragEnabled)
            {
                CancelDragArm();
                return;
            }

            Point curDip = e.GetPosition(_window);
            double dx = curDip.X - _pressPointDip.X;
            double dy = curDip.Y - _pressPointDip.Y;
            if ((dx * dx) + (dy * dy) < (DragThresholdDip * DragThresholdDip))
            {
                return;
            }

            IntPtr targetHwnd = _armedTargetHwnd;
            CancelDragArm();
            e.Handled = true;

            if (targetHwnd != IntPtr.Zero)
            {
                BeginRestoreAndDragMaximizedWindow(targetHwnd);
            }
        }

        private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            CancelDragArm();
        }

        private void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            _isArmed = false;
            _armedTargetHwnd = IntPtr.Zero;
        }

        private void CancelDragArm()
        {
            _isArmed = false;
            _armedTargetHwnd = IntPtr.Zero;
            if (_window.IsMouseCaptured)
            {
                _window.ReleaseMouseCapture();
            }
        }

        private bool TryHandleBlankDoubleClick()
        {
            IntPtr topBarHwnd = new WindowInteropHelper(_window).Handle;
            if (topBarHwnd == IntPtr.Zero)
            {
                return false;
            }

            if (TryFindTopMaximizedTarget(topBarHwnd, out IntPtr targetHwnd, out _))
            {
                _lastRestoredHwnd = targetHwnd;
                _lastRestoredTick = Environment.TickCount64;
                _ = SetForegroundWindow(targetHwnd);
                _ = PostMessageW(targetHwnd, WmSysCommand, new IntPtr(ScRestore), IntPtr.Zero);
                DiagnosticLog.Info($"Double-clicked blank TopBar to restore maximized hwnd=0x{targetHwnd.ToInt64():X}.");
                return true;
            }

            if (_lastRestoredHwnd != IntPtr.Zero &&
                Environment.TickCount64 - _lastRestoredTick < 15000 &&
                IsWindow(_lastRestoredHwnd) &&
                IsWindowVisible(_lastRestoredHwnd) &&
                !IsIconic(_lastRestoredHwnd) &&
                !IsZoomed(_lastRestoredHwnd) &&
                CanToggleMaximize(_lastRestoredHwnd) &&
                GetForegroundWindow() == _lastRestoredHwnd &&
                MonitorFromWindow(_lastRestoredHwnd, MonitorDefaultToNearest) == MonitorFromWindow(topBarHwnd, MonitorDefaultToNearest))
            {
                _ = PostMessageW(_lastRestoredHwnd, WmSysCommand, new IntPtr(ScMaximize), IntPtr.Zero);
                DiagnosticLog.Info($"Double-clicked blank TopBar to re-maximize hwnd=0x{_lastRestoredHwnd.ToInt64():X}.");
                _lastRestoredHwnd = IntPtr.Zero;
                return true;
            }

            return false;
        }

        private void BeginRestoreAndDragMaximizedWindow(IntPtr targetHwnd)
        {
            if (!IsWindow(targetHwnd) || !IsWindowVisible(targetHwnd) || IsIconic(targetHwnd))
            {
                return;
            }

            IntPtr topBarHwnd = new WindowInteropHelper(_window).Handle;
            if (topBarHwnd == IntPtr.Zero)
            {
                return;
            }

            IntPtr monitor = MonitorFromWindow(topBarHwnd, MonitorDefaultToNearest);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref mi) || !GetCursorPos(out POINT curPx))
            {
                return;
            }

            uint dpi = GetDpiForWindow(topBarHwnd);
            double dpiScale = dpi > 0 ? dpi / 96.0 : 1.0;

            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(targetHwnd, ref wp))
            {
                return;
            }

            RECT anchored = ComputeAnchoredRestoreRect(wp.rcNormalPosition, mi.rcMonitor, mi.rcWork, curPx, dpiScale);
            int width = anchored.Right - anchored.Left;
            int height = anchored.Bottom - anchored.Top;

            wp.flags = 0;
            wp.showCmd = SwShowNormal;
            wp.rcNormalPosition = anchored;
            _ = SetWindowPlacement(targetHwnd, ref wp);

            if (IsZoomed(targetHwnd))
            {
                _ = ShowWindow(targetHwnd, SwRestore);
            }

            _ = SetWindowPos(targetHwnd, IntPtr.Zero, anchored.Left, anchored.Top, width, height, SwpNoZOrder | SwpNoActivate);

            _lastRestoredHwnd = targetHwnd;
            _lastRestoredTick = Environment.TickCount64;

            _ = SetForegroundWindow(targetHwnd);

            // Re-sample cursor right after restore in case a few pixels elapsed, then shift window immediately if needed
            // and start lockstep 125Hz cursor tracking with zero delay.
            POINT trackingAnchorCursor = GetCursorPos(out POINT latestPx) ? latestPx : curPx;
            int shiftX = trackingAnchorCursor.X - curPx.X;
            int shiftY = trackingAnchorCursor.Y - curPx.Y;
            int startLeft = anchored.Left + shiftX;
            int startTop = Math.Max(mi.rcMonitor.Top + 2, anchored.Top + shiftY);
            if (shiftX != 0 || shiftY != 0)
            {
                _ = SetWindowPos(targetHwnd, IntPtr.Zero, startLeft, startTop, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos);
            }

            StartImmediateDrag(targetHwnd, trackingAnchorCursor, startLeft, startTop, mi.rcMonitor, mi.rcWork);
            DiagnosticLog.Info(
                $"Restored & started lockstep drag hwnd=0x{targetHwnd.ToInt64():X} at ({startLeft},{startTop} {width}x{height}).");
        }

        private void StartImmediateDrag(
            IntPtr targetHwnd,
            POINT anchorCursorPx,
            int startLeft,
            int startTop,
            RECT monRect,
            RECT workRect)
        {
            if (!IsWindow(targetHwnd) || !IsLeftButtonPhysicallyPressed())
            {
                return;
            }

            _fallbackDragHwnd = targetHwnd;
            _fallbackStartCursorPx = anchorCursorPx;
            _lastDragCursorPx = anchorCursorPx;
            _fallbackStartLeftPx = startLeft;
            _fallbackStartTopPx = startTop;
            _fallbackMonitorRect = monRect;
            _fallbackWorkRect = workRect;
            _fallbackDragActive = true;
            _fallbackDragTimer.Start();
        }

        private void OnFallbackDragTimerTick(object? sender, EventArgs e)
        {
            if (_disposed || !_fallbackDragActive || !IsWindow(_fallbackDragHwnd))
            {
                StopFallbackDrag(snapMaximizeIfAtTop: false);
                return;
            }

            if (!IsLeftButtonPhysicallyPressed())
            {
                StopFallbackDrag(snapMaximizeIfAtTop: true);
                return;
            }

            if (!GetCursorPos(out POINT curPx))
            {
                return;
            }

            if (curPx.X == _lastDragCursorPx.X && curPx.Y == _lastDragCursorPx.Y)
            {
                return;
            }

            _lastDragCursorPx = curPx;
            int nextLeft = _fallbackStartLeftPx + (curPx.X - _fallbackStartCursorPx.X);
            int nextTop = Math.Max(_fallbackMonitorRect.Top + 2, _fallbackStartTopPx + (curPx.Y - _fallbackStartCursorPx.Y));
            _ = SetWindowPos(_fallbackDragHwnd, IntPtr.Zero, nextLeft, nextTop, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos);
        }

        private void StopFallbackDrag(bool snapMaximizeIfAtTop)
        {
            _fallbackDragTimer.Stop();
            IntPtr hwnd = _fallbackDragHwnd;
            RECT mon = _fallbackMonitorRect;
            RECT work = _fallbackWorkRect;
            bool wasActive = _fallbackDragActive;
            _fallbackDragActive = false;
            _fallbackDragHwnd = IntPtr.Zero;

            if (!wasActive || !snapMaximizeIfAtTop || hwnd == IntPtr.Zero || !IsWindow(hwnd) || !GetCursorPos(out POINT curPx))
            {
                return;
            }

            if (curPx.Y <= mon.Top + 4)
            {
                _ = PostMessageW(hwnd, WmSysCommand, new IntPtr(ScMaximize), IntPtr.Zero);
                return;
            }

            int workW = Math.Max(400, work.Right - work.Left);
            int workH = Math.Max(300, work.Bottom - work.Top);
            if (curPx.X <= mon.Left + 4)
            {
                _ = SetWindowPos(hwnd, IntPtr.Zero, work.Left, work.Top, workW / 2, workH, SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos);
                return;
            }

            if (curPx.X >= mon.Right - 4)
            {
                int leftHalf = workW / 2;
                _ = SetWindowPos(hwnd, IntPtr.Zero, work.Left + leftHalf, work.Top, workW - leftHalf, workH, SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos);
                return;
            }

            if (GetWindowRect(hwnd, out RECT finalRect) && finalRect.Top < work.Top)
            {
                _ = SetWindowPos(hwnd, IntPtr.Zero, finalRect.Left, work.Top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos);
            }
        }

        private static bool TryFindTopMaximizedTarget(IntPtr topBarHwnd, out IntPtr targetHwnd, out double dpiScale)
        {
            targetHwnd = IntPtr.Zero;
            dpiScale = 1.0;

            if (topBarHwnd == IntPtr.Zero)
            {
                return false;
            }

            WindowSampleResult sample = NativeWindowSampler.SampleTargetMonitor(topBarHwnd, sampleColor: false);
            if (sample.Kind != WindowMatchKind.MaximizedWindow ||
                sample.Hwnd == IntPtr.Zero ||
                !CanToggleMaximize(sample.Hwnd))
            {
                return false;
            }

            targetHwnd = sample.Hwnd;
            dpiScale = sample.DpiScale > 0 ? sample.DpiScale : 1.0;
            return true;
        }

        private static bool CanToggleMaximize(IntPtr hwnd)
        {
            long style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
            if ((style & (WsChild | WsDisabled)) != 0)
            {
                return false;
            }

            return (style & (WsMaximizeBox | WsThickFrame)) != 0 || IsZoomed(hwnd);
        }

        private static bool IsLeftButtonPhysicallyPressed() => (GetAsyncKeyState(VkLButton) & 0x8000) != 0;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelDragArm();
            StopFallbackDrag(snapMaximizeIfAtTop: false);
            AdaptiveSettingsStore.TopBarMaximizedWindowDragChanged -= OnSettingChanged;
            _window.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            _window.PreviewMouseMove -= OnPreviewMouseMove;
            _window.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
            _window.LostMouseCapture -= OnLostMouseCapture;
        }

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

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int length;
            public uint flags;
            public uint showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
    }
}

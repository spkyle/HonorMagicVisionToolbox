using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;

namespace TopBarAdaptiveColor
{
    internal enum WindowMatchKind
    {
        None = 0,
        MaximizedWindow = 1,
        TrueFullscreen = 2
    }

    internal readonly struct WindowSampleResult
    {
        public WindowMatchKind Kind { get; init; }
        public IntPtr Hwnd { get; init; }
        public string? ProcessName { get; init; }
        public Color? SampledColor { get; init; }
        public bool KeepPreviousColor { get; init; }
        public int MonitorTopPx { get; init; }
        public int WorkTopPx { get; init; }
        public int MatchedFrameTopPx { get; init; }
        public int TopBarBottomPx { get; init; }
        public int TopBarWidthPx { get; init; }
        public double DpiScale { get; init; }
    }

    internal static class NativeWindowSampler
    {
        private const int MonitorDefaultToNearest = 2;
        private const int DwmwaExtendedFrameBounds = 9;
        private const int DwmwaCloaked = 14;
        private const int GwlExStyle = -20;
        private const long WsExTransparent = 0x00000020L;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExAppWindow = 0x00040000L;
        private const long WsExLayered = 0x00080000L;
        private const uint Srccopy = 0x00CC0020;
        private const uint CaptureBlt = 0x40000000;
        private const uint BiRgb = 0;
        private const uint DibRgbColors = 0;

        private static readonly int CurrentPid = Environment.ProcessId;

        [ThreadStatic]
        private static byte[]? _samplePixelBuffer;

        [ThreadStatic]
        private static Dictionary<int, (int Count, long SumR, long SumG, long SumB)>? _sampleBucketScratch;

        [ThreadStatic]
        private static int[]? _sampleRows2Scratch;

        [ThreadStatic]
        private static int[]? _sampleRows3Scratch;

        private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "Progman",
            "WorkerW",
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd",
            "DV2ControlHost",
            "TaskListThumbnailWnd",
            "ForegroundStaging",
            "ApplicationManager_DesktopShellWindow"
        };

        private static readonly HashSet<string> IgnoredProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "DesktopHost",
            "WallpaperHost",
            "MagicVisuals",
            "EntranceAnimation",
            "DesktopReturnButton",
            "MenuHostNative",
            "CardMenuHostNative",
            "MagicSpaceTurbo",
            "HonorDesktop",
            "ZoneHost",
            "SearchHost",
            "StartMenuExperienceHost",
            "ShellExperienceHost",
            "TextInputHost",
            "LockApp"
        };

        private static readonly ConcurrentDictionary<uint, (string Name, long Tick)> ProcessNameCache = new();

        public static WindowSampleResult SampleTargetMonitor(
            IntPtr topBarHwnd,
            bool sampleColor,
            IReadOnlyCollection<IntPtr>? excludedHwnds = null)
        {
            if (topBarHwnd == IntPtr.Zero || !IsWindow(topBarHwnd))
            {
                return default;
            }

            IntPtr monitor = MonitorFromWindow(topBarHwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return default;
            }

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref mi))
            {
                return default;
            }

            if (!GetWindowRect(topBarHwnd, out RECT topBarRect))
            {
                return default;
            }

            uint dpi = GetDpiForWindow(topBarHwnd);
            double dpiScale = dpi > 0 ? dpi / 96.0 : 1.0;

            RECT mon = mi.rcMonitor;
            RECT work = mi.rcWork;

            int minTopBarBottom = mon.Top + Math.Max(20, (int)Math.Round(24 * dpiScale));
            int maxTopBarBottom = mon.Top + (int)Math.Round(96 * dpiScale);
            int topBarBottomPx = Math.Clamp(topBarRect.Bottom, minTopBarBottom, maxTopBarBottom);

            int sampleY1 = topBarBottomPx + Math.Max(2, (int)Math.Round(2.5 * dpiScale));
            int sampleY2 = topBarBottomPx + Math.Max(4, (int)Math.Round(5.0 * dpiScale));
            if (sampleY2 >= mon.Bottom)
            {
                return default;
            }

            int tolX = Math.Max(8, (int)Math.Round(16 * dpiScale));
            int tolY = Math.Max(6, (int)Math.Round(12 * dpiScale));
            int minWindowHeight = Math.Max(48, (int)Math.Round(64 * dpiScale));

            int monWidth = mon.Right - mon.Left;
            int monHeight = mon.Bottom - mon.Top;
            int workHeight = Math.Max(1, work.Bottom - work.Top);

            IntPtr matchedHwnd = IntPtr.Zero;
            string? matchedProcName = null;
            RECT matchedRect = default;
            bool isTrueFullscreen = false;
            var occludingRects = new List<RECT>(4);

            EnumWindows((hwnd, _) =>
            {
                if (hwnd == topBarHwnd || !IsWindowVisible(hwnd) || IsIconic(hwnd))
                {
                    return true;
                }

                if (excludedHwnds != null && excludedHwnds.Count > 0)
                {
                    foreach (IntPtr ex in excludedHwnds)
                    {
                        if (ex == hwnd)
                        {
                            return true;
                        }
                    }
                }

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0 || pid == (uint)CurrentPid)
                {
                    return true;
                }

                if (IsWindowCloaked(hwnd))
                {
                    return true;
                }

                long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
                bool isToolOnly = (exStyle & WsExToolWindow) != 0 && (exStyle & WsExAppWindow) == 0;
                bool isClickThroughLayered = (exStyle & (WsExTransparent | WsExLayered)) == (WsExTransparent | WsExLayered);
                if (isToolOnly || isClickThroughLayered)
                {
                    return true;
                }

                string className = GetWindowClassNameSafe(hwnd);
                if (IgnoredClasses.Contains(className))
                {
                    // Stop walking if the desktop shell view itself is above lower windows in Z-order (e.g., Win+D).
                    if ((string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase)) &&
                        FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero &&
                        GetWindowRect(hwnd, out RECT desktopRect) &&
                        desktopRect.Left <= mon.Left + tolX &&
                        desktopRect.Right >= mon.Right - tolX)
                    {
                        return false;
                    }
                    return true;
                }

                string procName = GetProcessNameCached(pid);
                if (!string.IsNullOrEmpty(procName) && IgnoredProcessNames.Contains(procName))
                {
                    return true;
                }

                RECT frame = GetExtendedFrameRect(hwnd);
                int w = frame.Right - frame.Left;
                int h = frame.Bottom - frame.Top;
                if (w <= 0 || h <= 0)
                {
                    return true;
                }

                // Must overlap the target monitor horizontally.
                if (frame.Right <= mon.Left + tolX || frame.Left >= mon.Right - tolX)
                {
                    return true;
                }

                // True fullscreen covers the entire monitor including top edge.
                if (frame.Left <= mon.Left + 2 &&
                    frame.Right >= mon.Right - 2 &&
                    frame.Top <= mon.Top + 2 &&
                    frame.Bottom >= mon.Bottom - 2)
                {
                    isTrueFullscreen = true;
                    return false;
                }

                bool spansWidth = frame.Left <= mon.Left + tolX && frame.Right >= mon.Right - tolX;
                bool touchesTopBar = frame.Top <= topBarBottomPx + tolY && frame.Top >= mon.Top - tolY;
                bool coversWorkHeight = IsZoomed(hwnd) || h >= (int)(workHeight * 0.78);

                if (spansWidth && touchesTopBar && frame.Bottom >= topBarBottomPx + minWindowHeight && coversWorkHeight)
                {
                    matchedHwnd = hwnd;
                    matchedProcName = procName;
                    matchedRect = frame;
                    return false;
                }

                // Record non-maximized windows intersecting our sampling strip so we don't sample floating dialogs.
                if (frame.Top <= sampleY2 && frame.Bottom >= sampleY1)
                {
                    occludingRects.Add(frame);
                }

                return true;
            }, IntPtr.Zero);

            if (isTrueFullscreen)
            {
                return new WindowSampleResult { Kind = WindowMatchKind.TrueFullscreen };
            }

            if (matchedHwnd == IntPtr.Zero)
            {
                return new WindowSampleResult { Kind = WindowMatchKind.None };
            }

            int topBarWidthPx = Math.Max(0, topBarRect.Right - topBarRect.Left);

            if (!sampleColor)
            {
                return new WindowSampleResult
                {
                    Kind = WindowMatchKind.MaximizedWindow,
                    Hwnd = matchedHwnd,
                    ProcessName = matchedProcName,
                    MonitorTopPx = mon.Top,
                    WorkTopPx = work.Top,
                    MatchedFrameTopPx = matchedRect.Top,
                    TopBarBottomPx = topBarRect.Bottom,
                    TopBarWidthPx = topBarWidthPx,
                    DpiScale = dpiScale
                };
            }

            int leftBound = Math.Max(mon.Left, matchedRect.Left);
            int rightBound = Math.Min(mon.Right, matchedRect.Right);
            int spanW = rightBound - leftBound;
            if (spanW < 40)
            {
                return new WindowSampleResult
                {
                    Kind = WindowMatchKind.MaximizedWindow,
                    Hwnd = matchedHwnd,
                    ProcessName = matchedProcName,
                    KeepPreviousColor = true,
                    MonitorTopPx = mon.Top,
                    WorkTopPx = work.Top,
                    MatchedFrameTopPx = matchedRect.Top,
                    TopBarBottomPx = topBarRect.Bottom,
                    TopBarWidthPx = topBarWidthPx,
                    DpiScale = dpiScale
                };
            }

            int margin = Math.Max(20, spanW / 10);
            int xStart = leftBound + margin;
            int xEnd = rightBound - margin;
            if (xEnd <= xStart + 16)
            {
                xStart = leftBound + 4;
                xEnd = rightBound - 4;
            }

            int effectiveSampleBaseY = Math.Max(topBarBottomPx, matchedRect.Top);
            int effectiveSampleY1 = effectiveSampleBaseY + Math.Max(2, (int)Math.Round(2.5 * dpiScale));
            int effectiveSampleY2 = effectiveSampleBaseY + Math.Max(4, (int)Math.Round(5.0 * dpiScale));
            Color? color = effectiveSampleY2 < mon.Bottom
                ? SampleStripDominantColor(xStart, xEnd, effectiveSampleY1, effectiveSampleY2, occludingRects)
                : null;

            return new WindowSampleResult
            {
                Kind = WindowMatchKind.MaximizedWindow,
                Hwnd = matchedHwnd,
                ProcessName = matchedProcName,
                SampledColor = color,
                KeepPreviousColor = !color.HasValue,
                MonitorTopPx = mon.Top,
                WorkTopPx = work.Top,
                MatchedFrameTopPx = matchedRect.Top,
                TopBarBottomPx = topBarRect.Bottom,
                TopBarWidthPx = topBarWidthPx,
                DpiScale = dpiScale
            };
        }

        internal static bool HasRealFullscreenWindow(IntPtr topBarHwnd)
        {
            if (topBarHwnd == IntPtr.Zero)
            {
                return false;
            }

            IntPtr monitor = MonitorFromWindow(topBarHwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref mi))
            {
                return false;
            }

            RECT mon = mi.rcMonitor;
            bool found = false;

            EnumWindows((hwnd, _) =>
            {
                if (hwnd == topBarHwnd || !IsWindowVisible(hwnd) || IsIconic(hwnd) || IsWindowCloaked(hwnd))
                {
                    return true;
                }

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0 || pid == (uint)CurrentPid)
                {
                    return true;
                }

                long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
                bool isToolOnly = (exStyle & WsExToolWindow) != 0 && (exStyle & WsExAppWindow) == 0;
                bool isClickThroughLayered = (exStyle & (WsExTransparent | WsExLayered)) == (WsExTransparent | WsExLayered);
                if (isToolOnly || isClickThroughLayered)
                {
                    return true;
                }

                string className = GetWindowClassNameSafe(hwnd);
                if (IgnoredClasses.Contains(className))
                {
                    return true;
                }

                string procName = GetProcessNameCached(pid);
                if (!string.IsNullOrEmpty(procName) &&
                    (IgnoredProcessNames.Contains(procName) ||
                     (string.Equals(procName, "pixpin", StringComparison.OrdinalIgnoreCase) && (exStyle & WsExLayered) != 0)))
                {
                    return true;
                }

                RECT frame = GetExtendedFrameRect(hwnd);
                if (frame.Left <= mon.Left + 2 &&
                    frame.Right >= mon.Right - 2 &&
                    frame.Top <= mon.Top + 2 &&
                    frame.Bottom >= mon.Bottom - 2)
                {
                    found = true;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return found;
        }

        private static Color? SampleStripDominantColor(
            int xStart,
            int xEnd,
            int y1,
            int y2,
            List<RECT> occludingRects)
        {
            int width = xEnd - xStart;
            int height = (y2 - y1) + 1;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            IntPtr screenDc = IntPtr.Zero;
            IntPtr memDc = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldObj = IntPtr.Zero;

            try
            {
                screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero)
                {
                    return null;
                }

                memDc = CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero)
                {
                    return null;
                }

                var bmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = width,
                        biHeight = -height, // top-down DIB
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = BiRgb
                    }
                };

                hBitmap = CreateDIBSection(memDc, ref bmi, DibRgbColors, out IntPtr bitsPtr, IntPtr.Zero, 0);
                if (hBitmap == IntPtr.Zero || bitsPtr == IntPtr.Zero)
                {
                    return null;
                }

                oldObj = SelectObject(memDc, hBitmap);
                if (!BitBlt(memDc, 0, 0, width, height, screenDc, xStart, y1, Srccopy | CaptureBlt))
                {
                    return null;
                }

                int strideBytes = width * 4;
                int requiredBytes = strideBytes * height;
                byte[] pixels = _samplePixelBuffer != null && _samplePixelBuffer.Length >= requiredBytes
                    ? _samplePixelBuffer
                    : (_samplePixelBuffer = new byte[requiredBytes]);
                Marshal.Copy(bitsPtr, pixels, 0, requiredBytes);

                // Pick up to 64 evenly spaced columns, excluding occluded x positions.
                const int targetCols = 64;
                int step = Math.Max(1, width / targetCols);

                // Quantized 5-bit RGB bucket -> (count, sumR, sumG, sumB)
                Dictionary<int, (int Count, long SumR, long SumG, long SumB)> buckets =
                    _sampleBucketScratch ??= new Dictionary<int, (int Count, long SumR, long SumG, long SumB)>(32);
                buckets.Clear();

                int rowMid = height / 2;
                int[] rows;
                if (height >= 3)
                {
                    rows = _sampleRows3Scratch ??= new int[3];
                    rows[0] = 0;
                    rows[1] = rowMid;
                    rows[2] = height - 1;
                }
                else
                {
                    rows = _sampleRows2Scratch ??= new int[2];
                    rows[0] = 0;
                    rows[1] = height - 1;
                }

                int totalSamples = 0;
                for (int localX = 0; localX < width; localX += step)
                {
                    int screenX = xStart + localX;
                    bool occluded = false;
                    for (int i = 0; i < occludingRects.Count; i++)
                    {
                        var r = occludingRects[i];
                        if (screenX >= r.Left && screenX < r.Right)
                        {
                            occluded = true;
                            break;
                        }
                    }

                    if (occluded)
                    {
                        continue;
                    }

                    for (int ri = 0; ri < rows.Length; ri++)
                    {
                        int offset = rows[ri] * strideBytes + localX * 4;
                        byte b = pixels[offset];
                        byte g = pixels[offset + 1];
                        byte r = pixels[offset + 2];

                        int key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                        if (buckets.TryGetValue(key, out var entry))
                        {
                            buckets[key] = (entry.Count + 1, entry.SumR + r, entry.SumG + g, entry.SumB + b);
                        }
                        else
                        {
                            buckets[key] = (1, r, g, b);
                        }

                        totalSamples++;
                    }
                }

                if (totalSamples < 6 || buckets.Count == 0)
                {
                    return null;
                }

                (int Count, long SumR, long SumG, long SumB) best = default;
                foreach (var kv in buckets)
                {
                    if (kv.Value.Count > best.Count)
                    {
                        best = kv.Value;
                    }
                }

                if (best.Count <= 0)
                {
                    return null;
                }

                byte avgR = (byte)Math.Clamp((int)Math.Round((double)best.SumR / best.Count), 0, 255);
                byte avgG = (byte)Math.Clamp((int)Math.Round((double)best.SumG / best.Count), 0, 255);
                byte avgB = (byte)Math.Clamp((int)Math.Round((double)best.SumB / best.Count), 0, 255);
                return Color.FromRgb(avgR, avgG, avgB);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (oldObj != IntPtr.Zero && memDc != IntPtr.Zero)
                {
                    SelectObject(memDc, oldObj);
                }

                if (hBitmap != IntPtr.Zero)
                {
                    DeleteObject(hBitmap);
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

        private static bool IsWindowCloaked(IntPtr hwnd)
        {
            int hr = DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, Marshal.SizeOf<int>());
            return hr == 0 && cloaked != 0;
        }

        private static RECT GetExtendedFrameRect(IntPtr hwnd)
        {
            int hr = DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out RECT rect, Marshal.SizeOf<RECT>());
            if (hr == 0 && rect.Right > rect.Left && rect.Bottom > rect.Top)
            {
                return rect;
            }

            GetWindowRect(hwnd, out rect);
            return rect;
        }

        private static string GetWindowClassNameSafe(IntPtr hwnd)
        {
            var sb = new StringBuilder(128);
            int len = GetClassName(hwnd, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : string.Empty;
        }

        private static string GetProcessNameCached(uint pid)
        {
            long now = Environment.TickCount64;
            if (ProcessNameCache.TryGetValue(pid, out var cached) && (now - cached.Tick) < 15000)
            {
                return cached.Name;
            }

            string name = string.Empty;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                name = proc.ProcessName;
            }
            catch
            {
                name = string.Empty;
            }

            if (ProcessNameCache.Count > 256)
            {
                ProcessNameCache.Clear();
            }

            ProcessNameCache[pid] = (name, now);
            return name;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

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

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string? lpszWindow);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);
    }
}

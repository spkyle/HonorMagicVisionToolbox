using System;
using System.Linq.Expressions;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TopBarAdaptiveColor
{
    /// <summary>
    /// Adds native Windows Quick Link menu (Win + X) when right-clicking the Windows Start button
    /// ("utility:start") on Honor Magic Vision's bottom Dock (MagicTaskbarWpf.MainWindow).
    /// Supports both the primary NativeCompositionDockWindow backend (ItemContextRequested event)
    /// and a WPF PreviewMouseRightButtonUp fallback, with zero modification of official binaries.
    /// </summary>
    internal sealed class DockStartRightClickEnhancer : IDisposable
    {
        private const string StartItemId = "utility:start";
        private const long TriggerDebounceMs = 280;
        private const int MaxInitialAttachPolls = 30;

        private readonly Window _dockWindow;
        private readonly FieldInfo? _compositionNativeWindowField;
        private readonly DispatcherTimer _attachPollTimer;
        private readonly MouseButtonEventHandler _wpfRightUpHandler;

        private object? _hookedCompositionWindow;
        private EventInfo? _hookedItemContextEvent;
        private Delegate? _hookedItemContextDelegate;
        private PropertyInfo? _dockItemIdProp;
        private int _initialAttachPolls;
        private long _lastTriggerTick;
        private bool _disposed;

        public DockStartRightClickEnhancer(Window dockWindow)
        {
            _dockWindow = dockWindow ?? throw new ArgumentNullException(nameof(dockWindow));

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _compositionNativeWindowField = dockWindow.GetType().GetField("_compositionNativeWindow", flags);

            _attachPollTimer = new DispatcherTimer(DispatcherPriority.Background, dockWindow.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _attachPollTimer.Tick += OnAttachPollTimerTick;

            _wpfRightUpHandler = new MouseButtonEventHandler(OnDockPreviewMouseRightButtonUp);

            _dockWindow.Loaded += OnDockWindowLifecycleSignal;
            _dockWindow.IsVisibleChanged += OnDockWindowIsVisibleChanged;
            _dockWindow.LayoutUpdated += OnDockWindowLayoutUpdated;
            _dockWindow.Closed += OnDockWindowClosed;
            _dockWindow.AddHandler(
                UIElement.PreviewMouseRightButtonUpEvent,
                _wpfRightUpHandler,
                handledEventsToo: true);

            if (!TryEnsureCompositionHook())
            {
                _attachPollTimer.Start();
            }

            DiagnosticLog.Info(
                $"DockStartRightClickEnhancer attached to MainWindow (compField={_compositionNativeWindowField != null}, hooked={_hookedCompositionWindow != null}).");
        }

        private void OnAttachPollTimerTick(object? sender, EventArgs e)
        {
            if (_disposed)
            {
                _attachPollTimer.Stop();
                return;
            }

            _initialAttachPolls++;
            if (TryEnsureCompositionHook() || _initialAttachPolls >= MaxInitialAttachPolls)
            {
                _attachPollTimer.Stop();
            }
        }

        private void OnDockWindowLifecycleSignal(object sender, RoutedEventArgs e)
        {
            if (!_disposed)
            {
                TryEnsureCompositionHook();
            }
        }

        private void OnDockWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!_disposed && e.NewValue is true)
            {
                TryEnsureCompositionHook();
            }
        }

        private void OnDockWindowLayoutUpdated(object? sender, EventArgs e)
        {
            if (_disposed || _compositionNativeWindowField == null)
            {
                return;
            }

            // Re-hook transparently if DirectComposition host was recreated (e.g., display change / device loss).
            object? current = _compositionNativeWindowField.GetValue(_dockWindow);
            if (current != null && !ReferenceEquals(current, _hookedCompositionWindow))
            {
                TryEnsureCompositionHook();
            }
        }

        private bool TryEnsureCompositionHook()
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

                UnhookCurrentCompositionWindow();

                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                EventInfo? evt = ncdw.GetType().GetEvent("ItemContextRequested", flags);
                MethodInfo? addMethod = evt?.GetAddMethod(nonPublic: true);
                if (evt?.EventHandlerType == null || addMethod == null)
                {
                    return false;
                }

                Delegate? handler = BuildItemContextDelegate(evt.EventHandlerType);
                if (handler == null)
                {
                    return false;
                }

                // Use GetAddMethod(nonPublic: true) so both public and internal events attach cleanly.
                addMethod.Invoke(ncdw, new object[] { handler });
                _hookedCompositionWindow = ncdw;
                _hookedItemContextEvent = evt;
                _hookedItemContextDelegate = handler;
                DiagnosticLog.Info("DockStartRightClickEnhancer hooked NativeCompositionDockWindow.ItemContextRequested.");
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"DockStartRightClickEnhancer hook failed: {ex.Message}");
                return false;
            }
        }

        private Delegate? BuildItemContextDelegate(Type handlerType)
        {
            if (handlerType == typeof(Action<string>))
            {
                return (Action<string>)OnCompositionItemContextRequested;
            }

            MethodInfo? invoke = handlerType.GetMethod("Invoke");
            if (invoke == null)
            {
                return null;
            }

            ParameterInfo[] parameters = invoke.GetParameters();
            MethodInfo targetMethod = typeof(DockStartRightClickEnhancer).GetMethod(
                nameof(OnCompositionItemContextRequested),
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            if (parameters.Length == 1)
            {
                ParameterExpression p0 = Expression.Parameter(parameters[0].ParameterType, "itemId");
                Expression argExpr = parameters[0].ParameterType == typeof(string)
                    ? (Expression)p0
                    : Expression.Convert(p0, typeof(string));
                MethodCallExpression call = Expression.Call(Expression.Constant(this), targetMethod, argExpr);
                return Expression.Lambda(handlerType, call, p0).Compile();
            }

            if (parameters.Length == 2)
            {
                ParameterExpression p0 = Expression.Parameter(parameters[0].ParameterType, "sender");
                ParameterExpression p1 = Expression.Parameter(parameters[1].ParameterType, "itemId");
                Expression argExpr = parameters[1].ParameterType == typeof(string)
                    ? (Expression)p1
                    : Expression.Convert(p1, typeof(string));
                MethodCallExpression call = Expression.Call(Expression.Constant(this), targetMethod, argExpr);
                return Expression.Lambda(handlerType, call, p0, p1).Compile();
            }

            return null;
        }

        private void UnhookCurrentCompositionWindow()
        {
            if (_hookedCompositionWindow != null && _hookedItemContextEvent != null && _hookedItemContextDelegate != null)
            {
                try
                {
                    MethodInfo? removeMethod = _hookedItemContextEvent.GetRemoveMethod(nonPublic: true);
                    removeMethod?.Invoke(_hookedCompositionWindow, new object[] { _hookedItemContextDelegate });
                }
                catch
                {
                }
            }

            _hookedCompositionWindow = null;
            _hookedItemContextEvent = null;
            _hookedItemContextDelegate = null;
        }

        private void OnCompositionItemContextRequested(string? itemId)
        {
            if (_disposed || !AdaptiveSettingsStore.IsDockStartNativeMenuEnabled)
            {
                return;
            }

            if (!string.Equals(itemId, StartItemId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            TriggerNativeWinXMenu("composition");
        }

        private void OnDockPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_disposed || !AdaptiveSettingsStore.IsDockStartNativeMenuEnabled)
            {
                return;
            }

            try
            {
                if (IsStartItemVisual(e.OriginalSource as DependencyObject))
                {
                    e.Handled = true;
                    TriggerNativeWinXMenu("wpf-fallback");
                }
            }
            catch
            {
            }
        }

        private bool IsStartItemVisual(DependencyObject? source)
        {
            DependencyObject? cur = source;
            for (int depth = 0; depth < 12 && cur != null; depth++)
            {
                if (cur is FrameworkElement fe && fe.DataContext != null)
                {
                    object dc = fe.DataContext;
                    if (_dockItemIdProp == null || _dockItemIdProp.DeclaringType != dc.GetType())
                    {
                        PropertyInfo? p = dc.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);
                        if (p?.PropertyType == typeof(string))
                        {
                            _dockItemIdProp = p;
                        }
                    }

                    if (_dockItemIdProp != null && _dockItemIdProp.DeclaringType == dc.GetType())
                    {
                        string? id = _dockItemIdProp.GetValue(dc) as string;
                        if (string.Equals(id, StartItemId, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }

                cur = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur);
            }

            return false;
        }

        private void TriggerNativeWinXMenu(string source)
        {
            long now = Environment.TickCount64;
            long prev = Interlocked.Read(ref _lastTriggerTick);
            if (prev != 0 && now - prev < TriggerDebounceMs)
            {
                return;
            }

            Interlocked.Exchange(ref _lastTriggerTick, now);

            IntPtr dockHwnd = IntPtr.Zero;
            try
            {
                dockHwnd = new WindowInteropHelper(_dockWindow).Handle;
            }
            catch
            {
            }

            DiagnosticLog.Info($"DockStartRightClickEnhancer: opening native Windows Start menu (Win+X) via {source}.");
            ThreadPool.UnsafeQueueUserWorkItem(_ => SendWinXChordOnWorker(dockHwnd), null);
        }

        private static void SendWinXChordOnWorker(IntPtr dockHwnd)
        {
            try
            {
                // Wait briefly until the physical right mouse button is released so Explorer's Win+X menu
                // does not immediately misinterpret a lingering RMB state.
                for (int i = 0; i < 30; i++)
                {
                    if ((GetAsyncKeyState(VkRButton) & 0x8000) == 0)
                    {
                        break;
                    }
                    Thread.Sleep(10);
                }

                // Grant Shell / Explorer foreground permission so the native Win+X popup receives keyboard focus immediately.
                _ = AllowSetForegroundWindow(AsfwAny);
                IntPtr trayWnd = FindWindowW("Shell_TrayWnd", null);
                if (trayWnd != IntPtr.Zero)
                {
                    _ = GetWindowThreadProcessId(trayWnd, out uint shellPid);
                    if (shellPid != 0)
                    {
                        _ = AllowSetForegroundWindow(shellPid);
                    }
                }

                // If magictaskbar-ui is currently running at Medium IL (e.g. restarted via explorer.exe) while
                // an elevated Admin window owns the foreground, UIPI can block SendInput to that foreground window.
                // Move focus briefly to our just-clicked Dock window (or Progman) so SendInput reaches win32k cleanly.
                if (!IsCurrentProcessElevated() && IsForegroundWindowInaccessible())
                {
                    IntPtr fallbackTarget = dockHwnd != IntPtr.Zero ? dockHwnd : FindWindowW("Progman", null);
                    if (fallbackTarget != IntPtr.Zero)
                    {
                        _ = SetForegroundWindow(fallbackTarget);
                        Thread.Sleep(15);
                    }
                }

                ushort lwinScan = (ushort)MapVirtualKeyW(VkLWin, 0);
                ushort xScan = (ushort)MapVirtualKeyW(VkX, 0);

                INPUT[] downInputs =
                {
                    CreateKeyboardInput(VkLWin, lwinScan, KeyEventFExtendedKey),
                    CreateKeyboardInput(VkX, xScan, 0)
                };

                INPUT[] upInputs =
                {
                    CreateKeyboardInput(VkX, xScan, KeyEventFKeyUp),
                    CreateKeyboardInput(VkLWin, lwinScan, KeyEventFExtendedKey | KeyEventFKeyUp)
                };

                int inputSize = Marshal.SizeOf<INPUT>();
                uint sentDown = SendInput((uint)downInputs.Length, downInputs, inputSize);
                int downErr = sentDown == downInputs.Length ? 0 : Marshal.GetLastWin32Error();

                Thread.Sleep(25);

                uint sentUp = SendInput((uint)upInputs.Length, upInputs, inputSize);
                int upErr = sentUp == upInputs.Length ? 0 : Marshal.GetLastWin32Error();

                if (sentDown != downInputs.Length || sentUp != upInputs.Length)
                {
                    DiagnosticLog.Warn(
                        $"DockStartRightClickEnhancer: SendInput(Win+X) partial/failed down={sentDown}(err={downErr}) up={sentUp}(err={upErr}) cbSize={inputSize}.");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"DockStartRightClickEnhancer: SendWinXChordOnWorker failed: {ex.Message}");
            }
        }

        private static bool IsCurrentProcessElevated()
        {
            try
            {
                using WindowsIdentity id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsForegroundWindowInaccessible()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero)
            {
                return false;
            }

            _ = GetWindowThreadProcessId(fg, out uint fgPid);
            if (fgPid == 0 || fgPid == (uint)Environment.ProcessId)
            {
                return false;
            }

            IntPtr hProc = OpenProcess(ProcessQueryInformation, false, fgPid);
            if (hProc == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error() == ErrorAccessDenied;
            }

            CloseHandle(hProc);
            return false;
        }

        private static INPUT CreateKeyboardInput(ushort vk, ushort scan, uint flags)
        {
            return new INPUT
            {
                type = InputKeyboard,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = scan,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }

        private void OnDockWindowClosed(object? sender, EventArgs e)
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
            _attachPollTimer.Stop();
            _attachPollTimer.Tick -= OnAttachPollTimerTick;
            _dockWindow.Loaded -= OnDockWindowLifecycleSignal;
            _dockWindow.IsVisibleChanged -= OnDockWindowIsVisibleChanged;
            _dockWindow.LayoutUpdated -= OnDockWindowLayoutUpdated;
            _dockWindow.Closed -= OnDockWindowClosed;
            _dockWindow.RemoveHandler(UIElement.PreviewMouseRightButtonUpEvent, _wpfRightUpHandler);
            UnhookCurrentCompositionWindow();
        }

        #region Win32 Interop

        private const uint InputKeyboard = 1;
        private const uint KeyEventFExtendedKey = 0x0001;
        private const uint KeyEventFKeyUp = 0x0002;
        private const ushort VkLWin = 0x5B;
        private const ushort VkX = 0x58;
        private const int VkRButton = 0x02;
        private const uint AsfwAny = 0xFFFFFFFFu;
        private const uint ProcessQueryInformation = 0x0400;
        private const int ErrorAccessDenied = 5;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(uint dwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        #endregion
    }
}

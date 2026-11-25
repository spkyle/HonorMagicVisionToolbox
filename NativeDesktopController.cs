using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace TopBarAdaptiveColor
{
    /// <summary>
    /// Restores the native Windows desktop (explorer.exe SysListView32) while keeping Magic Vision's TopBar,
    /// Dock, Control Center, Notification Center, and window animations intact — with zero binary modification:
    /// - When enabled: gracefully sends WM_CLOSE to DesktopHost.exe's official message sink (HonorDesktop_HideWnd),
    ///   triggering DesktopHost's built-in RestoreSysListViewWindow shutdown path, verifies SysListView32 visibility,
    ///   and cleans up any orphaned MenuHostNative process. A lightweight background guard catches boot races or
    ///   restarts using a single FindWindowW check (~0% idle CPU).
    /// - When disabled (or Master Switch OFF / Reset Defaults): cleanly re-launches DesktopHost.exe at Medium IL
    ///   using explorer.exe's shell token (CreateProcessWithTokenW) so drag-and-drop UIPI matches official launches.
    /// </summary>
    internal static class NativeDesktopController
    {
        private const string DesktopHostProcessName = "DesktopHost";
        private const string MenuHostProcessName = "MenuHostNative";
        private const string HideWndClassName = "HonorDesktop_HideWnd_Class";
        private const string HideWndTitle = "HonorDesktop_HideWnd";
        private const string DefaultDesktopHostPath = @"C:\Program Files\HONOR\MagicAnimation\NewExplorer\DesktopHost.exe";

        private const int StartupBurstDurationMs = 15000;
        private const int StartupBurstIntervalMs = 1000;
        private const int SteadyPollIntervalMs = 5000;
        private const int BackoffPollIntervalMs = 30000;
        private const int MaxConsecutiveDismissFailures = 4;

        private static readonly object LifecycleSync = new();

        private static int _initialized;
        private static int _processExiting;
        private static int _enforceNativeDesktop;
        private static int _generation;

        private static ManualResetEvent? _stopEvent;
        private static AutoResetEvent? _wakeEvent;
        private static Thread? _watcherThread;
        private static volatile string? _cachedDesktopHostExePath;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 1)
            {
                return;
            }

            CacheDesktopHostPathIfRunning();

            AdaptiveSettingsStore.NativeWindowsDesktopChanged += OnSettingChanged;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            try
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"NativeDesktopController PowerModeChanged hook skipped: {ex.Message}");
            }

            bool enabled = AdaptiveSettingsStore.IsNativeWindowsDesktopEnabled;
            if (enabled)
            {
                StartEnforcement();
            }

            DiagnosticLog.Info($"NativeDesktopController initialized (enabled={enabled}).");
        }

        private static void OnSettingChanged(bool enabled)
        {
            if (Volatile.Read(ref _processExiting) == 1)
            {
                return;
            }

            if (enabled)
            {
                StartEnforcement();
            }
            else
            {
                int gen = StopEnforcement();
                ThreadPool.UnsafeQueueUserWorkItem(_ => RestoreHonorDesktopIfNeeded(gen), null);
            }
        }

        private static void OnProcessExit(object? sender, EventArgs e)
        {
            Volatile.Write(ref _processExiting, 1);
            try
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch
            {
            }

            // Never relaunch DesktopHost during host shutdown.
            _ = StopEnforcement();
        }

        private static void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume ||
                Volatile.Read(ref _enforceNativeDesktop) == 0 ||
                Volatile.Read(ref _processExiting) == 1)
            {
                return;
            }

            try
            {
                _wakeEvent?.Set();
            }
            catch
            {
            }
        }

        private static void StartEnforcement()
        {
            lock (LifecycleSync)
            {
                if (Volatile.Read(ref _processExiting) == 1)
                {
                    return;
                }

                Interlocked.Increment(ref _generation);
                if (Volatile.Read(ref _enforceNativeDesktop) == 1 && _watcherThread != null)
                {
                    try
                    {
                        _wakeEvent?.Set();
                    }
                    catch
                    {
                    }
                    return;
                }

                _stopEvent = new ManualResetEvent(false);
                _wakeEvent = new AutoResetEvent(false);
                Volatile.Write(ref _enforceNativeDesktop, 1);

                var state = (_stopEvent, _wakeEvent);
                _watcherThread = new Thread(EnforceLoop)
                {
                    Name = "NativeDesktopWatcher",
                    IsBackground = true
                };
                _watcherThread.Start(state);
                DiagnosticLog.Info("NativeDesktopController enforcement started.");
            }
        }

        private static int StopEnforcement()
        {
            Thread? watcher;
            ManualResetEvent? stopEvt;
            AutoResetEvent? wakeEvt;
            int gen;

            lock (LifecycleSync)
            {
                gen = Interlocked.Increment(ref _generation);
                if (Volatile.Read(ref _enforceNativeDesktop) == 0 && _watcherThread == null)
                {
                    return gen;
                }

                Volatile.Write(ref _enforceNativeDesktop, 0);
                stopEvt = _stopEvent;
                wakeEvt = _wakeEvent;
                watcher = _watcherThread;
                _stopEvent = null;
                _wakeEvent = null;
                _watcherThread = null;
            }

            try
            {
                stopEvt?.Set();
            }
            catch
            {
            }

            try
            {
                watcher?.Join(1500);
            }
            catch
            {
            }

            try
            {
                stopEvt?.Dispose();
            }
            catch
            {
            }

            try
            {
                wakeEvt?.Dispose();
            }
            catch
            {
            }

            DiagnosticLog.Info("NativeDesktopController enforcement stopped.");
            return gen;
        }

        private static void EnforceLoop(object? stateObj)
        {
            if (stateObj is not ValueTuple<ManualResetEvent, AutoResetEvent> events)
            {
                return;
            }

            ManualResetEvent stopEvt = events.Item1;
            AutoResetEvent wakeEvt = events.Item2;
            WaitHandle[] waitHandles = { stopEvt, wakeEvt };

            long startTick = Environment.TickCount64;
            int lastFailedPid = 0;
            int consecutiveFailures = 0;
            bool firstPass = true;

            try
            {
                while (Volatile.Read(ref _enforceNativeDesktop) == 1 && !stopEvt.WaitOne(0))
                {
                    try
                    {
                        bool inStartupBurst = Environment.TickCount64 - startTick < StartupBurstDurationMs;
                        IntPtr hideWnd = FindHonorDesktopHideWindow();

                        if (hideWnd != IntPtr.Zero)
                        {
                            if (!firstPass)
                            {
                                // Brief settle so a freshly-launched DesktopHost finishes wiring its WM_CLOSE loop.
                                if (stopEvt.WaitOne(350))
                                {
                                    break;
                                }
                            }

                            int dismissedPid = DismissDesktopHostGracefully(stopEvt, out bool exited);
                            if (exited)
                            {
                                lastFailedPid = 0;
                                consecutiveFailures = 0;
                                CleanupOrphanedMenuHost(stopEvt);
                                EnsureNativeSysListViewVisible();
                            }
                            else if (dismissedPid != 0)
                            {
                                if (dismissedPid == lastFailedPid)
                                {
                                    consecutiveFailures++;
                                }
                                else
                                {
                                    lastFailedPid = dismissedPid;
                                    consecutiveFailures = 1;
                                }

                                if (consecutiveFailures >= MaxConsecutiveDismissFailures)
                                {
                                    DiagnosticLog.Warn(
                                        $"NativeDesktopController: DesktopHost (pid={dismissedPid}) ignored {consecutiveFailures} WM_CLOSE requests; backing off.");
                                }
                            }
                        }
                        else if (firstPass || inStartupBurst)
                        {
                            // DesktopHost may still be booting before creating HonorDesktop_HideWnd; or it may already
                            // have exited leaving an orphaned prewarmed MenuHostNative or hidden SysListView32.
                            int desktopPid = FindFirstProcessId(DesktopHostProcessName);
                            if (desktopPid == 0)
                            {
                                if (firstPass)
                                {
                                    CleanupOrphanedMenuHost(stopEvt);
                                    EnsureNativeSysListViewVisible();
                                }
                            }
                            else
                            {
                                CacheDesktopHostPath(desktopPid);
                            }
                        }

                        firstPass = false;

                        int waitMs;
                        if (consecutiveFailures >= MaxConsecutiveDismissFailures)
                        {
                            waitMs = BackoffPollIntervalMs;
                        }
                        else if (Environment.TickCount64 - startTick < StartupBurstDurationMs)
                        {
                            waitMs = StartupBurstIntervalMs;
                        }
                        else
                        {
                            waitMs = SteadyPollIntervalMs;
                        }

                        int signaled = WaitHandle.WaitAny(waitHandles, waitMs);
                        if (signaled == 0)
                        {
                            break;
                        }

                        if (signaled == 1)
                        {
                            // Woken on resume or re-enable: re-enter fast check window briefly.
                            startTick = Environment.TickCount64 - (StartupBurstDurationMs / 2);
                            consecutiveFailures = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Warn($"NativeDesktopController loop error: {ex.Message}");
                        if (stopEvt.WaitOne(5000))
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"NativeDesktopController watcher exited: {ex.Message}");
            }
        }

        private static int DismissDesktopHostGracefully(ManualResetEvent stopEvt, out bool exited)
        {
            exited = false;
            List<IntPtr> hideWindows = FindAllHonorDesktopHideWindows();
            if (hideWindows.Count == 0)
            {
                exited = FindFirstProcessId(DesktopHostProcessName) == 0;
                return 0;
            }

            int primaryPid = 0;
            foreach (IntPtr hwnd in hideWindows)
            {
                _ = GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != 0)
                {
                    if (primaryPid == 0)
                    {
                        primaryPid = (int)pid;
                    }
                    CacheDesktopHostPath((int)pid);
                }

                bool posted = PostMessageW(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                DiagnosticLog.Info(
                    $"NativeDesktopController: posted WM_CLOSE to HonorDesktop_HideWnd (hwnd=0x{hwnd.ToInt64():X}, pid={pid}, ok={posted}).");
            }

            // Wait up to 2.5s for DesktopHost to finish RestoreSysListViewWindow and exit cleanly.
            long deadline = Environment.TickCount64 + 2500;
            while (Environment.TickCount64 < deadline)
            {
                if (stopEvt.WaitOne(100))
                {
                    return primaryPid;
                }

                if (FindFirstProcessId(DesktopHostProcessName) == 0)
                {
                    exited = true;
                    DiagnosticLog.Info("NativeDesktopController: DesktopHost exited cleanly; native Windows desktop restored.");
                    return primaryPid;
                }
            }

            exited = FindFirstProcessId(DesktopHostProcessName) == 0;
            return primaryPid;
        }

        private static void CleanupOrphanedMenuHost(ManualResetEvent stopEvt)
        {
            if (FindFirstProcessId(DesktopHostProcessName) != 0)
            {
                return;
            }

            Process[] menuProcs = Array.Empty<Process>();
            try
            {
                menuProcs = Process.GetProcessesByName(MenuHostProcessName);
                if (menuProcs.Length == 0)
                {
                    return;
                }

                // Give DesktopHost's tracked shutdown a brief window before closing any pre-warmed orphan.
                if (stopEvt.WaitOne(350))
                {
                    return;
                }

                if (FindFirstProcessId(DesktopHostProcessName) != 0)
                {
                    return;
                }

                foreach (Process proc in menuProcs)
                {
                    try
                    {
                        if (proc.HasExited)
                        {
                            continue;
                        }

                        int pid = proc.Id;
                        PostCloseToProcessWindows((uint)pid);
                        if (!proc.WaitForExit(1000) && !proc.HasExited && FindFirstProcessId(DesktopHostProcessName) == 0)
                        {
                            proc.Kill();
                            DiagnosticLog.Info($"NativeDesktopController: terminated orphaned MenuHostNative (pid={pid}).");
                        }
                        else
                        {
                            DiagnosticLog.Info($"NativeDesktopController: closed orphaned MenuHostNative (pid={pid}).");
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Warn($"NativeDesktopController: MenuHostNative cleanup skipped: {ex.Message}");
                    }
                }
            }
            catch
            {
            }
            finally
            {
                for (int i = 0; i < menuProcs.Length; i++)
                {
                    try
                    {
                        menuProcs[i].Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void EnsureNativeSysListViewVisible()
        {
            try
            {
                if (IsUserDesktopIconsHiddenByExplorerSetting())
                {
                    return;
                }

                IntPtr listView = FindNativeDesktopListView();
                if (listView != IntPtr.Zero && !IsWindowVisible(listView))
                {
                    _ = ShowWindowAsync(listView, SwShow);
                    DiagnosticLog.Info($"NativeDesktopController: restored hidden SysListView32 visibility (hwnd=0x{listView.ToInt64():X}).");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"NativeDesktopController: EnsureNativeSysListViewVisible skipped: {ex.Message}");
            }
        }

        private static bool IsUserDesktopIconsHiddenByExplorerSetting()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                    writable: false);
                object? val = key?.GetValue("HideIcons");
                if (val is int i)
                {
                    return i != 0;
                }
            }
            catch
            {
            }

            return false;
        }

        private static IntPtr FindNativeDesktopListView()
        {
            IntPtr progman = FindWindowW("Progman", null);
            if (progman != IntPtr.Zero)
            {
                IntPtr defView = FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero)
                {
                    IntPtr lv = FindWindowExW(defView, IntPtr.Zero, "SysListView32", null);
                    if (lv != IntPtr.Zero)
                    {
                        return lv;
                    }
                }
            }

            IntPtr found = IntPtr.Zero;
            EnumWindows((hwnd, _) =>
            {
                if (string.Equals(GetWindowClassName(hwnd), "WorkerW", StringComparison.Ordinal))
                {
                    IntPtr defView = FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (defView != IntPtr.Zero)
                    {
                        IntPtr lv = FindWindowExW(defView, IntPtr.Zero, "SysListView32", null);
                        if (lv != IntPtr.Zero)
                        {
                            found = lv;
                            return false;
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);

            return found;
        }

        private static void RestoreHonorDesktopIfNeeded(int expectedGeneration)
        {
            try
            {
                if (Volatile.Read(ref _processExiting) == 1 ||
                    Volatile.Read(ref _enforceNativeDesktop) == 1 ||
                    Volatile.Read(ref _generation) != expectedGeneration)
                {
                    return;
                }

                if (FindFirstProcessId(DesktopHostProcessName) != 0)
                {
                    DiagnosticLog.Info("NativeDesktopController: DesktopHost is already running; skip relaunch.");
                    return;
                }

                string? exePath = ResolveDesktopHostExePath();
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    DiagnosticLog.Warn("NativeDesktopController: DesktopHost.exe path not found; cannot relaunch.");
                    return;
                }

                if (Volatile.Read(ref _generation) != expectedGeneration || Volatile.Read(ref _enforceNativeDesktop) == 1)
                {
                    return;
                }

                if (TryLaunchAtShellMediumPrivilege(exePath, out uint pid))
                {
                    DiagnosticLog.Info($"NativeDesktopController: restored DesktopHost at Medium IL (pid={pid}).");
                    return;
                }

                // Fallback: if already running non-elevated, direct start is Medium IL; otherwise delegate to explorer.exe.
                if (!IsCurrentProcessElevated())
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                        UseShellExecute = true
                    };
                    using Process? p = Process.Start(psi);
                    DiagnosticLog.Info($"NativeDesktopController: restored DesktopHost via direct launch (pid={p?.Id ?? 0}).");
                }
                else
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{exePath}\"",
                        UseShellExecute = false
                    };
                    using Process? p = Process.Start(psi);
                    DiagnosticLog.Info("NativeDesktopController: restored DesktopHost via explorer.exe fallback.");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"NativeDesktopController: RestoreHonorDesktopIfNeeded failed: {ex.Message}");
            }
        }

        private static bool TryLaunchAtShellMediumPrivilege(string exePath, out uint launchedPid)
        {
            launchedPid = 0;
            IntPtr hShellProc = IntPtr.Zero;
            IntPtr hToken = IntPtr.Zero;
            IntPtr hPrimaryToken = IntPtr.Zero;
            IntPtr envBlock = IntPtr.Zero;

            try
            {
                IntPtr shellWnd = GetShellWindow();
                if (shellWnd == IntPtr.Zero)
                {
                    return false;
                }

                _ = GetWindowThreadProcessId(shellWnd, out uint shellPid);
                if (shellPid == 0)
                {
                    return false;
                }

                hShellProc = OpenProcess(ProcessQueryInformation, false, shellPid);
                if (hShellProc == IntPtr.Zero)
                {
                    hShellProc = OpenProcess(ProcessQueryLimitedInformation, false, shellPid);
                }

                if (hShellProc == IntPtr.Zero)
                {
                    return false;
                }

                if (!OpenProcessToken(hShellProc, TokenDuplicate | TokenQuery, out hToken))
                {
                    return false;
                }

                if (!DuplicateTokenEx(
                    hToken,
                    TokenAssignPrimary | TokenDuplicate | TokenQuery | TokenAdjustDefault | TokenAdjustSessionId,
                    IntPtr.Zero,
                    SecurityImpersonation,
                    TokenTypePrimary,
                    out hPrimaryToken))
                {
                    return false;
                }

                bool hasEnv = CreateEnvironmentBlock(out envBlock, hPrimaryToken, bInherit: false);
                uint creationFlags = hasEnv ? CreateUnicodeEnvironment : 0u;
                string? workDir = Path.GetDirectoryName(exePath);
                var cmdLine = new StringBuilder($"\"{exePath}\"");

                var si = new STARTUPINFOW
                {
                    cb = Marshal.SizeOf<STARTUPINFOW>(),
                    lpDesktop = @"winsta0\default"
                };

                if (!CreateProcessWithTokenW(
                    hPrimaryToken,
                    0,
                    exePath,
                    cmdLine,
                    creationFlags,
                    hasEnv ? envBlock : IntPtr.Zero,
                    workDir,
                    ref si,
                    out PROCESS_INFORMATION pi))
                {
                    DiagnosticLog.Warn($"NativeDesktopController: CreateProcessWithTokenW(DesktopHost) failed err={Marshal.GetLastWin32Error()}.");
                    return false;
                }

                launchedPid = pi.dwProcessId;
                if (pi.hThread != IntPtr.Zero)
                {
                    CloseHandle(pi.hThread);
                }
                if (pi.hProcess != IntPtr.Zero)
                {
                    CloseHandle(pi.hProcess);
                }

                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"NativeDesktopController: TryLaunchAtShellMediumPrivilege failed: {ex.Message}");
                return false;
            }
            finally
            {
                if (envBlock != IntPtr.Zero)
                {
                    _ = DestroyEnvironmentBlock(envBlock);
                }
                if (hPrimaryToken != IntPtr.Zero)
                {
                    CloseHandle(hPrimaryToken);
                }
                if (hToken != IntPtr.Zero)
                {
                    CloseHandle(hToken);
                }
                if (hShellProc != IntPtr.Zero)
                {
                    CloseHandle(hShellProc);
                }
            }
        }

        private static bool IsCurrentProcessElevated()
        {
            try
            {
                using WindowsIdentity id = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(id);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static void CacheDesktopHostPathIfRunning()
        {
            int pid = FindFirstProcessId(DesktopHostProcessName);
            if (pid != 0)
            {
                CacheDesktopHostPath(pid);
            }
        }

        private static void CacheDesktopHostPath(int pid)
        {
            if (!string.IsNullOrEmpty(_cachedDesktopHostExePath))
            {
                return;
            }

            string? path = TryQueryProcessImagePath(pid);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                _cachedDesktopHostExePath = path;
            }
        }

        private static string? ResolveDesktopHostExePath()
        {
            string? cached = _cachedDesktopHostExePath;
            if (!string.IsNullOrEmpty(cached) && File.Exists(cached))
            {
                return cached;
            }

            try
            {
                string sibling = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "NewExplorer", "DesktopHost.exe"));
                if (File.Exists(sibling))
                {
                    _cachedDesktopHostExePath = sibling;
                    return sibling;
                }
            }
            catch
            {
            }

            if (File.Exists(DefaultDesktopHostPath))
            {
                _cachedDesktopHostExePath = DefaultDesktopHostPath;
                return DefaultDesktopHostPath;
            }

            return null;
        }

        private static IntPtr FindHonorDesktopHideWindow()
        {
            IntPtr exact = FindWindowW(HideWndClassName, HideWndTitle);
            if (exact != IntPtr.Zero)
            {
                return exact;
            }

            return FindWindowW(HideWndClassName, null);
        }

        private static List<IntPtr> FindAllHonorDesktopHideWindows()
        {
            var result = new List<IntPtr>();
            IntPtr exact = FindWindowW(HideWndClassName, HideWndTitle);
            if (exact != IntPtr.Zero)
            {
                result.Add(exact);
            }

            EnumWindows((hwnd, _) =>
            {
                if (string.Equals(GetWindowClassName(hwnd), HideWndClassName, StringComparison.Ordinal) &&
                    !result.Contains(hwnd))
                {
                    result.Add(hwnd);
                }
                return true;
            }, IntPtr.Zero);

            return result;
        }

        private static void PostCloseToProcessWindows(uint targetPid)
        {
            EnumWindows((hwnd, lParam) =>
            {
                _ = GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == targetPid)
                {
                    _ = PostMessageW(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                }
                return true;
            }, IntPtr.Zero);
        }

        private static int FindFirstProcessId(string processName)
        {
            Process[] procs = Array.Empty<Process>();
            try
            {
                procs = Process.GetProcessesByName(processName);
                return procs.Length > 0 ? procs[0].Id : 0;
            }
            catch
            {
                return 0;
            }
            finally
            {
                for (int i = 0; i < procs.Length; i++)
                {
                    try
                    {
                        procs[i].Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static string? TryQueryProcessImagePath(int pid)
        {
            IntPtr hProc = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (hProc == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var sb = new StringBuilder(512);
                uint len = (uint)sb.Capacity;
                return QueryFullProcessImageNameW(hProc, 0, sb, ref len) ? sb.ToString() : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                CloseHandle(hProc);
            }
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(128);
            int len = GetClassNameW(hwnd, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : string.Empty;
        }

        #region Win32 Interop

        private const uint WmClose = 0x0010;
        private const int SwShow = 5;

        private const uint ProcessQueryInformation = 0x0400;
        private const uint ProcessQueryLimitedInformation = 0x1000;

        private const uint TokenAssignPrimary = 0x0001;
        private const uint TokenDuplicate = 0x0002;
        private const uint TokenQuery = 0x0008;
        private const uint TokenAdjustDefault = 0x0080;
        private const uint TokenAdjustSessionId = 0x0100;
        private const int SecurityImpersonation = 2;
        private const int TokenTypePrimary = 1;
        private const uint CreateUnicodeEnvironment = 0x00000400;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOW
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageNameW(
            IntPtr hProcess,
            uint dwFlags,
            StringBuilder lpExeName,
            ref uint lpdwSize);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(
            IntPtr hExistingToken,
            uint dwDesiredAccess,
            IntPtr lpTokenAttributes,
            int impersonationLevel,
            int tokenType,
            out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessWithTokenW(
            IntPtr hToken,
            uint dwLogonFlags,
            string? lpApplicationName,
            StringBuilder? lpCommandLine,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            [In] ref STARTUPINFOW lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowExW(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        #endregion
    }
}

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace TopBarAdaptiveColor
{
    internal static class DiagnosticLog
    {
        private static readonly object FileSync = new();
        private static readonly ConcurrentQueue<string> PendingLines = new();
        private static readonly string LogPath = Path.Combine(
            Path.GetTempPath(), "MagicTaskbar", "adaptive-topbar.log");
        private static int _flushScheduled;
        private static bool _initialized;

        public static void Info(string message) => Enqueue("INFO", message);
        public static void Warn(string message) => Enqueue("WARN", message);

        private static void Enqueue(string level, string message)
        {
            try
            {
                if (PendingLines.Count > 256)
                {
                    return;
                }

                PendingLines.Enqueue(
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}][{level}] {message}{Environment.NewLine}");

                if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
                {
                    ThreadPool.QueueUserWorkItem(FlushWorker);
                }
            }
            catch
            {
                // Never let diagnostic logging disrupt host execution.
            }
        }

        private static void FlushWorker(object? _)
        {
            try
            {
                while (true)
                {
                    var sb = new StringBuilder(256);
                    int count = 0;
                    while (count < 64 && PendingLines.TryDequeue(out string? line))
                    {
                        sb.Append(line);
                        count++;
                    }

                    if (count > 0)
                    {
                        lock (FileSync)
                        {
                            if (!_initialized)
                            {
                                _initialized = true;
                                string? dir = Path.GetDirectoryName(LogPath);
                                if (!string.IsNullOrEmpty(dir))
                                {
                                    Directory.CreateDirectory(dir);
                                }

                                var fi = new FileInfo(LogPath);
                                if (fi.Exists && fi.Length > 512 * 1024)
                                {
                                    File.WriteAllText(LogPath, string.Empty);
                                }
                            }

                            File.AppendAllText(LogPath, sb.ToString());
                        }
                    }

                    Interlocked.Exchange(ref _flushScheduled, 0);
                    if (PendingLines.IsEmpty || Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0)
                    {
                        return;
                    }
                }
            }
            catch
            {
                Interlocked.Exchange(ref _flushScheduled, 0);
            }
        }
    }
}

/// <summary>
/// .NET runtime startup hook entry point (must reside in global namespace).
/// </summary>
public static class StartupHook
{
    private static Timer? _bootstrapTimer;
    private static int _hookAttached;

    public static void Initialize()
    {
        TopBarAdaptiveColor.DiagnosticLog.Info("StartupHook.Initialize entered.");

        if ((TopBarAdaptiveColor.AdaptiveSettingsStore.IsFastSettingsResponseEnabled ||
             TopBarAdaptiveColor.AdaptiveSettingsStore.IsFastNotificationResponseEnabled) &&
            !TopBarAdaptiveColor.AdaptiveSettingsStore.IsFastOpenIdleMemoryTrimEnabled)
        {
            Environment.SetEnvironmentVariable("MAGICTASKBAR_DISABLE_WORKING_SET_TRIM", "1");
            TopBarAdaptiveColor.DiagnosticLog.Info("Set MAGICTASKBAR_DISABLE_WORKING_SET_TRIM=1 before host startup.");
        }

        // Poll lightly until HostApp has loaded PresentationFramework and MagicTaskbar.TopBar.
        // Avoid touching any WPF type before PresentationFramework is loaded by the host.
        _bootstrapTimer = new Timer(_ => TryAttachWhenReady(), null, 100, 150);
    }

    private static void TryAttachWhenReady()
    {
        if (Volatile.Read(ref _hookAttached) == 1)
        {
            return;
        }

        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            bool hasWpf = assemblies.Any(a => string.Equals(a.GetName().Name, "PresentationFramework", StringComparison.OrdinalIgnoreCase));
            bool hasTopBar = assemblies.Any(a => string.Equals(a.GetName().Name, "MagicTaskbar.TopBar", StringComparison.OrdinalIgnoreCase));
            if (!hasWpf || !hasTopBar)
            {
                return;
            }

            if (Interlocked.Exchange(ref _hookAttached, 1) == 1)
            {
                return;
            }

            _bootstrapTimer?.Dispose();
            _bootstrapTimer = null;

            TopBarAdaptiveColor.TouchpadThreeFingerSearchController.Initialize();
            TopBarAdaptiveColor.TopBarHookCoordinator.ScheduleAttach();
        }
        catch (Exception ex)
        {
            TopBarAdaptiveColor.DiagnosticLog.Warn($"Bootstrap probe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

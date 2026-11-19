using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TopBarAdaptiveColor
{
    internal static class AdaptiveSettingsStore
    {
        private static readonly object Sync = new();
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "taskbarAndAIbar",
            "adaptive-topbar.json");

        private static readonly Dictionary<string, bool> ModuleStates = new(StringComparer.Ordinal);
        private static bool _loaded;

        public static event Action<bool>? EnabledChanged;
        public static event Action<bool>? GapFixChanged;
        public static event Action<bool>? YoyoClawChanged;
        public static event Action<bool>? NotificationAlertAnimationChanged;
        public static event Action<bool>? FastSettingsChanged;
        public static event Action<bool>? FastNotificationChanged;
        public static event Action<bool>? NativeTrayRightClickChanged;
        public static event Action<bool>? HiddenTrayIconPinningChanged;
        public static event Action<bool>? TopBarMaximizedWindowDragChanged;
        public static event Action<bool>? DockPreviewKeepOpenChanged;
        public static event Action<bool>? DockPreviewAeroPeekChanged;
        public static event Action<bool>? DockPreviewTransferCorridorChanged;
        public static event Action<bool>? TopBarPopupEdgeClampChanged;
        public static event Action<bool>? TouchpadThreeFingerSearchChanged;
        public static event Action<string, bool>? ModuleChanged;

        public static bool IsEnabled => IsModuleEnabled(ToolboxModuleIds.AdaptiveTopBarColor, true);

        public static bool IsGapFixEnabled => IsModuleEnabled(ToolboxModuleIds.AdaptiveTopBarGapFix, true);

        public static bool IsYoyoClawVisible => IsModuleEnabled(ToolboxModuleIds.ShowYoyoClawButton, true);

        public static bool IsNotificationAlertAnimationEnabled => IsModuleEnabled(ToolboxModuleIds.NotificationAlertAnimation, true);

        public static bool IsFastSettingsResponseEnabled => IsModuleEnabled(ToolboxModuleIds.FastSettingsResponse, true);

        public static bool IsFastNotificationResponseEnabled => IsModuleEnabled(ToolboxModuleIds.FastNotificationResponse, true);

        public static bool IsNativeTrayRightClickEnabled => IsModuleEnabled(ToolboxModuleIds.NativeTrayRightClick, true);

        public static bool IsHiddenTrayIconPinningEnabled => IsModuleEnabled(ToolboxModuleIds.HiddenTrayIconPinning, true);

        public static bool IsTopBarMaximizedWindowDragEnabled => IsModuleEnabled(ToolboxModuleIds.TopBarMaximizedWindowDrag, true);

        public static bool IsDockPreviewKeepOpenOnCloseEnabled => IsModuleEnabled(ToolboxModuleIds.DockPreviewKeepOpenOnClose, true);

        public static bool IsDockPreviewAeroPeekEnabled => IsModuleEnabled(ToolboxModuleIds.DockPreviewAeroPeek, true);

        public static bool IsDockPreviewTransferCorridorEnabled => IsModuleEnabled(ToolboxModuleIds.DockPreviewTransferCorridor, true);

        public static bool IsTopBarPopupEdgeClampEnabled => IsModuleEnabled(ToolboxModuleIds.TopBarPopupEdgeClamp, true);

        public static bool IsTouchpadThreeFingerSearchEnabled => IsModuleEnabled(ToolboxModuleIds.TouchpadThreeFingerSearch, true);

        public static bool IsModuleEnabled(string moduleId, bool defaultValue = true)
        {
            if (string.IsNullOrEmpty(moduleId))
            {
                return defaultValue;
            }

            lock (Sync)
            {
                EnsureLoadedLocked();
                return ModuleStates.TryGetValue(moduleId, out bool enabled) ? enabled : defaultValue;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            SetModuleEnabled(ToolboxModuleIds.AdaptiveTopBarColor, enabled);
        }

        public static void SetModuleEnabled(string moduleId, bool enabled)
        {
            if (string.IsNullOrEmpty(moduleId))
            {
                return;
            }

            bool changed;
            lock (Sync)
            {
                EnsureLoadedLocked();
                bool previous = ModuleStates.TryGetValue(moduleId, out bool existing)
                    ? existing
                    : GetDefaultEnabled(moduleId);
                changed = previous != enabled;
                ModuleStates[moduleId] = enabled;
                SaveLocked();
            }

            DiagnosticLog.Info($"Toolbox setting updated: module={moduleId} enabled={enabled}");
            if (changed)
            {
                NotifyModuleChanged(moduleId, enabled);
            }
        }

        public static void ResetAllToDefaults()
        {
            var notifications = new List<(string Id, bool Enabled)>();
            lock (Sync)
            {
                EnsureLoadedLocked();
                foreach (var mod in ToolboxModuleRegistry.Modules)
                {
                    if (mod.ControlKind != ToolboxControlKind.Toggle)
                    {
                        continue;
                    }

                    bool previous = ModuleStates.TryGetValue(mod.Id, out bool existing)
                        ? existing
                        : mod.DefaultEnabled;
                    ModuleStates[mod.Id] = mod.DefaultEnabled;
                    if (previous != mod.DefaultEnabled)
                    {
                        notifications.Add((mod.Id, mod.DefaultEnabled));
                    }
                }
                SaveLocked();
            }

            DiagnosticLog.Info($"Reset toolbox settings to defaults (changedCount={notifications.Count}).");
            foreach (var (id, enabled) in notifications)
            {
                NotifyModuleChanged(id, enabled);
            }
        }

        private static bool GetDefaultEnabled(string moduleId)
        {
            foreach (var mod in ToolboxModuleRegistry.Modules)
            {
                if (string.Equals(mod.Id, moduleId, StringComparison.Ordinal))
                {
                    return mod.DefaultEnabled;
                }
            }
            return true;
        }

        private static void NotifyModuleChanged(string moduleId, bool enabled)
        {
            ModuleChanged?.Invoke(moduleId, enabled);

            if (string.Equals(moduleId, ToolboxModuleIds.AdaptiveTopBarColor, StringComparison.Ordinal))
            {
                EnabledChanged?.Invoke(enabled);
            }
            else if (string.Equals(moduleId, ToolboxModuleIds.AdaptiveTopBarGapFix, StringComparison.Ordinal))
            {
                GapFixChanged?.Invoke(enabled);
            }
            else if (string.Equals(moduleId, ToolboxModuleIds.ShowYoyoClawButton, StringComparison.Ordinal))
            {
                YoyoClawChanged?.Invoke(enabled);
            }
            else if (string.Equals(moduleId, ToolboxModuleIds.NotificationAlertAnimation, StringComparison.Ordinal))
            {
                NotificationAlertAnimationChanged?.Invoke(enabled);
            }
            else if (string.Equals(moduleId, ToolboxModuleIds.FastSettingsResponse, StringComparison.Ordinal))
            {
                FastSettingsChanged?.Invoke(enabled);
            }
            else if (string.Equals(moduleId, ToolboxModuleIds.FastNotificationResponse, StringComparison.Ordinal))
            {
                FastNotificationChanged?.Invoke(enabled);
            }
            else if (string.Equals(moduleId, ToolboxModuleIds.NativeTrayRightClick, StringComparison.Ordinal))
            {
                NativeTrayRightClickChanged?.Invoke(enabled);
            }
            else if (string.Equals(moduleId, ToolboxModuleIds.HiddenTrayIconPinning, StringComparison.Ordinal))
            {
                HiddenTrayIconPinningChanged?.Invoke(enabled);
            }
            else if (string.Equals(moduleId, ToolboxModuleIds.TopBarMaximizedWindowDrag, StringComparison.Ordinal))
            {
                TopBarMaximizedWindowDragChanged?.Invoke(enabled);
            }
            else if (string.Equals(moduleId, ToolboxModuleIds.DockPreviewKeepOpenOnClose, StringComparison.Ordinal))
            {
                DockPreviewKeepOpenChanged?.Invoke(enabled);
            }
            else if (string.Equals(moduleId, ToolboxModuleIds.DockPreviewAeroPeek, StringComparison.Ordinal))
            {
                DockPreviewAeroPeekChanged?.Invoke(enabled);
            }
            else if (string.Equals(moduleId, ToolboxModuleIds.DockPreviewTransferCorridor, StringComparison.Ordinal))
            {
                DockPreviewTransferCorridorChanged?.Invoke(enabled);
            }
            else if (string.Equals(moduleId, ToolboxModuleIds.TopBarPopupEdgeClamp, StringComparison.Ordinal))
            {
                TopBarPopupEdgeClampChanged?.Invoke(enabled);
            }
            else if (string.Equals(moduleId, ToolboxModuleIds.TouchpadThreeFingerSearch, StringComparison.Ordinal))
            {
                TouchpadThreeFingerSearchChanged?.Invoke(enabled);
            }
        }

        private static void EnsureLoadedLocked()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            foreach (var mod in ToolboxModuleRegistry.Modules)
            {
                if (mod.ControlKind == ToolboxControlKind.Toggle)
                {
                    ModuleStates[mod.Id] = mod.DefaultEnabled;
                }
            }

            try
            {
                if (!File.Exists(ConfigPath))
                {
                    return;
                }

                string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                if (doc.RootElement.TryGetProperty("enabled", out var legacyProp) &&
                    (legacyProp.ValueKind == JsonValueKind.True || legacyProp.ValueKind == JsonValueKind.False))
                {
                    ModuleStates[ToolboxModuleIds.AdaptiveTopBarColor] = legacyProp.GetBoolean();
                }

                if (doc.RootElement.TryGetProperty("modules", out var modulesProp) &&
                    modulesProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in modulesProp.EnumerateObject())
                    {
                        if (entry.Value.ValueKind == JsonValueKind.True || entry.Value.ValueKind == JsonValueKind.False)
                        {
                            ModuleStates[entry.Name] = entry.Value.GetBoolean();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"Failed to load {ConfigPath}: {ex.Message}");
            }
        }

        private static void SaveLocked()
        {
            try
            {
                string? dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                bool legacyEnabled = ModuleStates.TryGetValue(ToolboxModuleIds.AdaptiveTopBarColor, out bool en)
                    ? en
                    : true;

                var payload = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["enabled"] = legacyEnabled,
                    ["modules"] = new SortedDictionary<string, bool>(ModuleStates, StringComparer.Ordinal)
                };

                string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true
                }) + Environment.NewLine;
                string tmpPath = ConfigPath + ".tmp";
                File.WriteAllText(tmpPath, json, new UTF8Encoding(false));
                File.Move(tmpPath, ConfigPath, overwrite: true);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"Failed to save {ConfigPath}: {ex.Message}");
            }
        }
    }
}

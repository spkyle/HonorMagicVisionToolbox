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
        private static bool _masterEnabled = true;
        private static bool _loaded;

        public static event Action<bool>? MasterEnabledChanged;
        public static event Action<bool>? NativeWindowsDesktopChanged;
        public static event Action<bool>? DisableTopBarChanged;
        public static event Action<bool>? DisableDockChanged;
        public static event Action<bool>? EnabledChanged;
        public static event Action<bool>? GapFixChanged;
        public static event Action<bool>? YoyoClawChanged;
        public static event Action<bool>? NotificationAlertAnimationChanged;
        public static event Action<bool>? FastSettingsChanged;
        public static event Action<bool>? FastNotificationChanged;
        public static event Action<bool>? FastOpenSoftwareRenderingChanged;
        public static event Action<bool>? FastOpenIdleMemoryTrimChanged;
        public static event Action<bool>? NativeTrayRightClickChanged;
        public static event Action<bool>? HiddenTrayIconPinningChanged;
        public static event Action<bool>? TopBarMaximizedWindowDragChanged;
        public static event Action<bool>? DockStartNativeMenuChanged;
        public static event Action<bool>? DockHighRefreshRateChanged;
        public static event Action<bool>? DockPreviewKeepOpenChanged;
        public static event Action<bool>? DockPreviewAeroPeekChanged;
        public static event Action<bool>? DockPreviewTransferCorridorChanged;
        public static event Action<bool>? TopBarPopupEdgeClampChanged;
        public static event Action<bool>? FastDesktopRightClickChanged;
        public static event Action<bool>? TouchpadThreeFingerSearchChanged;
        public static event Action<string, bool>? ModuleChanged;

        public static bool IsMasterEnabled
        {
            get
            {
                lock (Sync)
                {
                    EnsureLoadedLocked();
                    return _masterEnabled;
                }
            }
        }

        public static bool IsNativeWindowsDesktopEnabled => IsModuleEnabled(ToolboxModuleIds.NativeWindowsDesktop, false);

        public static bool IsTopBarDisabled => IsModuleEnabled(ToolboxModuleIds.DisableTopBar, false);

        public static bool IsDockDisabled => IsModuleEnabled(ToolboxModuleIds.DisableDock, false);

        public static bool IsEnabled => IsModuleEnabled(ToolboxModuleIds.AdaptiveTopBarColor, true);

        public static bool IsGapFixEnabled => IsModuleEnabled(ToolboxModuleIds.AdaptiveTopBarGapFix, true);

        public static bool IsYoyoClawVisible => IsModuleEnabled(ToolboxModuleIds.ShowYoyoClawButton, true);

        public static bool IsNotificationAlertAnimationEnabled => IsModuleEnabled(ToolboxModuleIds.NotificationAlertAnimation, true);

        public static bool IsFastSettingsResponseEnabled => IsModuleEnabled(ToolboxModuleIds.FastSettingsResponse, true);

        public static bool IsFastNotificationResponseEnabled => IsModuleEnabled(ToolboxModuleIds.FastNotificationResponse, true);

        public static bool IsFastOpenSoftwareRenderingEnabled => IsModuleEnabled(ToolboxModuleIds.FastOpenSoftwareRendering, true);

        public static bool IsFastOpenIdleMemoryTrimEnabled => IsModuleEnabled(ToolboxModuleIds.FastOpenIdleMemoryTrim, true);

        public static bool IsNativeTrayRightClickEnabled => IsModuleEnabled(ToolboxModuleIds.NativeTrayRightClick, true);

        public static bool IsHiddenTrayIconPinningEnabled => IsModuleEnabled(ToolboxModuleIds.HiddenTrayIconPinning, true);

        public static bool IsTopBarMaximizedWindowDragEnabled => IsModuleEnabled(ToolboxModuleIds.TopBarMaximizedWindowDrag, true);

        public static bool IsDockStartNativeMenuEnabled => IsModuleEnabled(ToolboxModuleIds.DockStartNativeMenu, true);

        public static bool IsDockHighRefreshRateEnabled => IsModuleEnabled(ToolboxModuleIds.DockHighRefreshRate, true);

        public static bool IsDockPreviewKeepOpenOnCloseEnabled => IsModuleEnabled(ToolboxModuleIds.DockPreviewKeepOpenOnClose, true);

        public static bool IsDockPreviewAeroPeekEnabled => IsModuleEnabled(ToolboxModuleIds.DockPreviewAeroPeek, true);

        public static bool IsDockPreviewTransferCorridorEnabled => IsModuleEnabled(ToolboxModuleIds.DockPreviewTransferCorridor, true);

        public static bool IsTopBarPopupEdgeClampEnabled => IsModuleEnabled(ToolboxModuleIds.TopBarPopupEdgeClamp, true);

        public static bool IsFastDesktopRightClickEnabled => IsModuleEnabled(ToolboxModuleIds.FastDesktopRightClick, true);

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
                bool configured = ModuleStates.TryGetValue(moduleId, out bool enabled) ? enabled : defaultValue;
                return GetEffectiveModuleValueLocked(moduleId, configured);
            }
        }

        public static bool IsModuleConfiguredEnabled(string moduleId, bool defaultValue = true)
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

        public static void SetMasterEnabled(bool enabled)
        {
            var effectiveChanges = new List<(string Id, bool EffectiveEnabled)>();
            bool masterChanged;

            lock (Sync)
            {
                EnsureLoadedLocked();
                masterChanged = _masterEnabled != enabled;
                if (!masterChanged)
                {
                    return;
                }

                var before = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var mod in ToolboxModuleRegistry.Modules)
                {
                    if (mod.ControlKind != ToolboxControlKind.Toggle)
                    {
                        continue;
                    }

                    bool configured = ModuleStates.TryGetValue(mod.Id, out bool existing) ? existing : mod.DefaultEnabled;
                    before[mod.Id] = GetEffectiveModuleValueLocked(mod.Id, configured);
                }

                _masterEnabled = enabled;

                foreach (var mod in ToolboxModuleRegistry.Modules)
                {
                    if (mod.ControlKind != ToolboxControlKind.Toggle)
                    {
                        continue;
                    }

                    bool configured = ModuleStates.TryGetValue(mod.Id, out bool existing) ? existing : mod.DefaultEnabled;
                    bool after = GetEffectiveModuleValueLocked(mod.Id, configured);
                    if (before.TryGetValue(mod.Id, out bool prev) && prev != after)
                    {
                        effectiveChanges.Add((mod.Id, after));
                    }
                }

                SaveLocked();
            }

            DiagnosticLog.Info($"Toolbox master switch updated: masterEnabled={enabled}, affectedModules={effectiveChanges.Count}");
            MasterEnabledChanged?.Invoke(enabled);
            foreach (var (id, eff) in effectiveChanges)
            {
                NotifyModuleChanged(id, eff);
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

            bool effectiveChanged;
            bool newEffective;
            lock (Sync)
            {
                EnsureLoadedLocked();
                bool previousConfigured = ModuleStates.TryGetValue(moduleId, out bool existing)
                    ? existing
                    : GetDefaultEnabled(moduleId);
                bool previousEffective = GetEffectiveModuleValueLocked(moduleId, previousConfigured);
                ModuleStates[moduleId] = enabled;
                newEffective = GetEffectiveModuleValueLocked(moduleId, enabled);
                effectiveChanged = previousEffective != newEffective;
                SaveLocked();
            }

            DiagnosticLog.Info($"Toolbox setting updated: module={moduleId} configured={enabled} effective={newEffective}");
            if (effectiveChanged)
            {
                NotifyModuleChanged(moduleId, newEffective);
            }
        }

        public static void ResetAllToDefaults()
        {
            var notifications = new List<(string Id, bool Enabled)>();
            bool masterChanged;
            lock (Sync)
            {
                EnsureLoadedLocked();
                var before = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var mod in ToolboxModuleRegistry.Modules)
                {
                    if (mod.ControlKind != ToolboxControlKind.Toggle)
                    {
                        continue;
                    }

                    bool prevConfigured = ModuleStates.TryGetValue(mod.Id, out bool existing)
                        ? existing
                        : mod.DefaultEnabled;
                    before[mod.Id] = GetEffectiveModuleValueLocked(mod.Id, prevConfigured);
                }

                masterChanged = !_masterEnabled;
                _masterEnabled = true;

                foreach (var mod in ToolboxModuleRegistry.Modules)
                {
                    if (mod.ControlKind != ToolboxControlKind.Toggle)
                    {
                        continue;
                    }

                    ModuleStates[mod.Id] = mod.DefaultEnabled;
                    bool after = GetEffectiveModuleValueLocked(mod.Id, mod.DefaultEnabled);
                    if (before.TryGetValue(mod.Id, out bool prev) && prev != after)
                    {
                        notifications.Add((mod.Id, after));
                    }
                }
                SaveLocked();
            }

            DiagnosticLog.Info($"Reset toolbox settings to defaults (masterChanged={masterChanged}, changedCount={notifications.Count}).");
            if (masterChanged)
            {
                MasterEnabledChanged?.Invoke(true);
            }
            foreach (var (id, enabled) in notifications)
            {
                NotifyModuleChanged(id, enabled);
            }
        }

        private static bool GetEffectiveModuleValueLocked(string moduleId, bool configuredValue)
        {
            if (_masterEnabled)
            {
                return configuredValue;
            }

            // Modules where true represents Honor's unmodified official behavior revert to true when toolbox is paused.
            if (string.Equals(moduleId, ToolboxModuleIds.ShowYoyoClawButton, StringComparison.Ordinal) ||
                string.Equals(moduleId, ToolboxModuleIds.TouchpadThreeFingerSearch, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
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

            if (string.Equals(moduleId, ToolboxModuleIds.NativeWindowsDesktop, StringComparison.Ordinal))
            {
                NativeWindowsDesktopChanged?.Invoke(enabled);
            }
            else if (string.Equals(moduleId, ToolboxModuleIds.DisableTopBar, StringComparison.Ordinal))
            {
                DisableTopBarChanged?.Invoke(enabled);
            }
            else if (string.Equals(moduleId, ToolboxModuleIds.DisableDock, StringComparison.Ordinal))
            {
                DisableDockChanged?.Invoke(enabled);
            }
            else if (string.Equals(moduleId, ToolboxModuleIds.AdaptiveTopBarColor, StringComparison.Ordinal))
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
            else if (string.Equals(moduleId, ToolboxModuleIds.FastOpenSoftwareRendering, StringComparison.Ordinal))
            {
                FastOpenSoftwareRenderingChanged?.Invoke(enabled);
            }
            else if (string.Equals(moduleId, ToolboxModuleIds.FastOpenIdleMemoryTrim, StringComparison.Ordinal))
            {
                FastOpenIdleMemoryTrimChanged?.Invoke(enabled);
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
            else if (string.Equals(moduleId, ToolboxModuleIds.DockStartNativeMenu, StringComparison.Ordinal))
            {
                DockStartNativeMenuChanged?.Invoke(enabled);
            }
            else if (string.Equals(moduleId, ToolboxModuleIds.DockHighRefreshRate, StringComparison.Ordinal))
            {
                DockHighRefreshRateChanged?.Invoke(enabled);
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
            else if (string.Equals(moduleId, ToolboxModuleIds.FastDesktopRightClick, StringComparison.Ordinal))
            {
                FastDesktopRightClickChanged?.Invoke(enabled);
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

                if (doc.RootElement.TryGetProperty("masterEnabled", out var masterProp) &&
                    (masterProp.ValueKind == JsonValueKind.True || masterProp.ValueKind == JsonValueKind.False))
                {
                    _masterEnabled = masterProp.GetBoolean();
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

                if (!_masterEnabled &&
                    doc.RootElement.TryGetProperty("savedStates", out var savedProp) &&
                    savedProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in savedProp.EnumerateObject())
                    {
                        if (entry.Name.StartsWith("saved:", StringComparison.Ordinal) &&
                            (entry.Value.ValueKind == JsonValueKind.True || entry.Value.ValueKind == JsonValueKind.False))
                        {
                            ModuleStates[entry.Name.Substring("saved:".Length)] = entry.Value.GetBoolean();
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

                var effectiveModules = new SortedDictionary<string, bool>(StringComparer.Ordinal);
                foreach (var kv in ModuleStates)
                {
                    effectiveModules[kv.Key] = GetEffectiveModuleValueLocked(kv.Key, kv.Value);
                }

                bool legacyEnabled = effectiveModules.TryGetValue(ToolboxModuleIds.AdaptiveTopBarColor, out bool en)
                    ? en
                    : _masterEnabled;

                var payload = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["masterEnabled"] = _masterEnabled,
                    ["enabled"] = legacyEnabled,
                    ["modules"] = effectiveModules
                };

                if (!_masterEnabled)
                {
                    // Prefix keys with "saved:" so external regex readers (ControlCenterSpeedBoost / MessageCenterBridge)
                    // strictly match only the effective flags in "modules".
                    var savedStates = new SortedDictionary<string, bool>(StringComparer.Ordinal);
                    foreach (var kv in ModuleStates)
                    {
                        savedStates["saved:" + kv.Key] = kv.Value;
                    }
                    payload["savedStates"] = savedStates;
                }

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

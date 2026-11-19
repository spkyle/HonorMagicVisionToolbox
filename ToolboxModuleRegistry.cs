using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;

namespace TopBarAdaptiveColor
{
    internal enum ToolboxCategory
    {
        StatusBarEnhancements = 0,
        DockEnhancements = 1,
        BugFixes = 2,
        Maintenance = 3
    }

    internal enum ToolboxControlKind
    {
        Toggle = 0,
        Action = 1
    }

    internal static class ToolboxModuleIds
    {
        public const string AdaptiveTopBarColor = "adaptive_topbar_color";
        public const string ShowYoyoClawButton = "show_yoyo_claw_button";
        public const string NativeTrayRightClick = "native_tray_right_click";
        public const string HiddenTrayIconPinning = "hidden_tray_icon_pinning";
        public const string TopBarMaximizedWindowDrag = "topbar_maximized_window_drag";
        public const string DockPreviewKeepOpenOnClose = "dock_preview_keep_open_on_close";
        public const string DockPreviewAeroPeek = "dock_preview_aero_peek";
        public const string DockPreviewTransferCorridor = "dock_preview_transfer_corridor";
        public const string AdaptiveTopBarGapFix = "adaptive_topbar_gap_fix";
        public const string TopBarPopupEdgeClamp = "topbar_popup_edge_clamp";
        public const string FastSettingsResponse = "fast_settings_response";
        public const string FastNotificationResponse = "fast_notification_response";
        public const string TouchpadThreeFingerSearch = "touchpad_three_finger_search";
        public const string ActionOpenLogs = "action_open_logs";
        public const string ActionResetDefaults = "action_reset_defaults";
        public const string ActionUninstall = "action_uninstall";
    }

    internal sealed class ToolboxCategoryMeta
    {
        public ToolboxCategory Category { get; init; }
        public string TitleZh { get; init; } = string.Empty;
        public string TitleEn { get; init; } = string.Empty;
    }

    internal sealed class ToolboxModuleDescriptor
    {
        public string Id { get; init; } = string.Empty;
        public ToolboxCategory Category { get; init; }
        public ToolboxControlKind ControlKind { get; init; } = ToolboxControlKind.Toggle;
        public string TitleZh { get; init; } = string.Empty;
        public string TitleEn { get; init; } = string.Empty;
        public string DescriptionZh { get; init; } = string.Empty;
        public string DescriptionEn { get; init; } = string.Empty;
        public bool DefaultEnabled { get; init; } = true;
        public string? ActionTextZh { get; init; }
        public string? ActionTextEn { get; init; }
        public bool IsDestructiveAction { get; init; }
        public Action? ActionHandler { get; init; }
    }

    internal static class ToolboxModuleRegistry
    {
        public static readonly IReadOnlyList<ToolboxCategoryMeta> Categories = new ToolboxCategoryMeta[]
        {
            new()
            {
                Category = ToolboxCategory.StatusBarEnhancements,
                TitleZh = "状态栏增强",
                TitleEn = "Status Bar Enhancements"
            },
            new()
            {
                Category = ToolboxCategory.DockEnhancements,
                TitleZh = "底部任务栏多任务增强",
                TitleEn = "Taskbar & Multitasking"
            },
            new()
            {
                Category = ToolboxCategory.BugFixes,
                TitleZh = "问题修复与优化",
                TitleEn = "Fixes & Optimizations"
            },
            new()
            {
                Category = ToolboxCategory.Maintenance,
                TitleZh = "工具与维护",
                TitleEn = "Tools & Maintenance"
            }
        };

        public static readonly IReadOnlyList<ToolboxModuleDescriptor> Modules = new ToolboxModuleDescriptor[]
        {
            new()
            {
                Id = ToolboxModuleIds.AdaptiveTopBarColor,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "全屏时状态栏自适应颜色",
                TitleEn = "Adaptive top bar color for maximized windows",
                DescriptionZh = "固定模式下窗口最大化后，自动匹配窗口顶部颜色并自适应深浅文字与图标",
                DescriptionEn = "Match maximized window header color and adapt light/dark foreground in fixed mode",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.ShowYoyoClawButton,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "显示右侧 YOYO Claw 按钮",
                TitleEn = "Show YOYO Claw button on top bar",
                DescriptionZh = "控制状态栏右侧控制中心旁的 YOYO Claw 快捷图标显示，关闭后将其隐藏",
                DescriptionEn = "Show or hide the YOYO Claw button beside Control Center on the top bar",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.NativeTrayRightClick,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "状态栏通知图标右键联动原生菜单",
                TitleEn = "Forward right-click on top bar app icons to native tray menu",
                DescriptionZh = "右键顶部微信、飞书等固定通知图标时自动触发原程序的系统托盘右键菜单（按住 Shift 右键或未找到时仍显示「从顶部栏移除」）",
                DescriptionEn = "Relay right-clicks on fixed app notification icons to their Windows tray menu (hold Shift+RightClick for Remove from Top Bar)",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.HiddenTrayIconPinning,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "支持拖拽任意隐藏托盘图标固定到顶部栏",
                TitleEn = "Drag any hidden tray icon to pin on top bar",
                DescriptionZh = "点击顶部 ^ 展开隐藏托盘后，可将任意应用图标直接拖放到顶部状态栏固定显示，支持重启恢复、左键激活、右键菜单及 Shift+右键移除",
                DescriptionEn = "Drag any icon from the overflow tray onto the top bar to pin it with persistence, left-click activation, native right-click, and Shift+RightClick removal",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.TopBarMaximizedWindowDrag,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "顶栏空白处拖拽与双击控制最大化窗口",
                TitleEn = "Drag or double-click blank top bar to restore & move maximized window",
                DescriptionZh = "在顶部状态栏空白区域按住左键向任意方向拖拽超过 6px，自动还原当前最大化窗口并跟手移动；双击空白处可直接还原（再双击可重新最大化）",
                DescriptionEn = "Hold left-click in the blank top bar strip and drag >6px in any direction to restore and drag the maximized window, or double-click to toggle restore",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.DockPreviewKeepOpenOnClose,
                Category = ToolboxCategory.DockEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "中键关闭单窗口后保持多窗口缩略图展开",
                TitleEn = "Keep thumbnail strip open after middle-closing one window",
                DescriptionZh = "在底部 Dock 多窗口预览中按鼠标中键或点 × 关闭某个窗口后，立即移除该卡片并保持其余窗口缩略图展开，方便连续关闭",
                DescriptionEn = "Remove only the closed window card on middle-click/close and keep remaining stacked thumbnails open",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.DockPreviewAeroPeek,
                Category = ToolboxCategory.DockEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "悬停缩略图卡片时桌面实时预览 (Aero Peek)",
                TitleEn = "Live desktop Aero Peek when hovering thumbnail cards",
                DescriptionZh = "鼠标悬停底部多窗口缩略图卡片时，在桌面临时完整展示对应窗口原画面（含最小化窗口），移开自动还原",
                DescriptionEn = "Preview the hovered window full-size on the desktop while keeping thumbnails on top; restore on leave",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.DockPreviewTransferCorridor,
                Category = ToolboxCategory.DockEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "底部 Dock 多窗口预览斜向防误关走廊",
                TitleEn = "Protect Dock multi-window preview when moving diagonally across divider or icons",
                DescriptionZh = "鼠标从底部 Dock 图标斜向滑向缩略图卡片时，经过分割线 | 或短暂擦碰相邻图标不会立即误关或切走预览",
                DescriptionEn = "Prevent accidental preview dismissal or premature switching when crossing the separator or brushing adjacent Dock icons toward preview cards",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.TopBarPopupEdgeClamp,
                Category = ToolboxCategory.BugFixes,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "修复状态栏靠右弹窗超出屏幕被裁切",
                TitleEn = "Clamp top bar popups inside monitor bounds",
                DescriptionZh = "隐藏 YOYO Claw 等按钮后电池、音量、网络等弹窗右移时，自动限制其与亚克力背景层不超出屏幕右边缘",
                DescriptionEn = "Prevent battery/volume/network popups and acrylic backdrops from clipping past the screen edge",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.AdaptiveTopBarGapFix,
                Category = ToolboxCategory.BugFixes,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "修复最大化后状态栏下方细缝",
                DescriptionZh = "工作区重算导致最大化窗口下移时，自动向下延展状态栏消除壁纸缝隙",
                TitleEn = "Fix top bar bottom seam after maximizing",
                DescriptionEn = "Extend top bar bottom edge by 1-3px when workspace reservation shifts windows down",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.FastSettingsResponse,
                Category = ToolboxCategory.BugFixes,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "状态栏控制中心与设置点击响应加速",
                TitleEn = "Boost Control Center & Settings click responsiveness",
                DescriptionZh = "改为按下即触发、保持控制中心与设置页热驻留并异步延后重扫描，显著降低点击弹出延迟",
                DescriptionEn = "Trigger on press, keep Control Center & Settings warm in memory, and defer heavy pre-show scans",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.FastNotificationResponse,
                Category = ToolboxCategory.BugFixes,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "顶部通知按钮极速响应与假死自愈",
                TitleEn = "Boost top bar Notification button response & auto-recover",
                DescriptionZh = "通知按钮按下即触发并直连唤醒已运行通知中心，若通知进程异常无响应则自动重建后打开",
                DescriptionEn = "Trigger on press, wake existing MessageCenter directly without respawning, and auto-recover if stuck",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.TouchpadThreeFingerSearch,
                Category = ToolboxCategory.BugFixes,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "允许压力触控板三指轻触弹出智慧搜索",
                TitleEn = "Allow three-finger touchpad light touch to open AI Search",
                DescriptionZh = "关闭后将单独禁用压力触控板三指轻触触发「智慧搜索」(TripleFingerLightTouch)，不影响三指拖拽或其他手势",
                DescriptionEn = "Turn off to disable three-finger Force Touch opening HONOR AI Search while keeping all other gestures intact",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.ActionOpenLogs,
                Category = ToolboxCategory.Maintenance,
                ControlKind = ToolboxControlKind.Action,
                TitleZh = "运行诊断日志",
                TitleEn = "Diagnostic logs",
                DescriptionZh = "打开自适应工具箱日志所在目录，便于排查和反馈问题",
                DescriptionEn = "Open the diagnostic log folder for troubleshooting",
                ActionTextZh = "打开目录",
                ActionTextEn = "Open Folder",
                ActionHandler = OpenDiagnosticLogFolder
            },
            new()
            {
                Id = ToolboxModuleIds.ActionResetDefaults,
                Category = ToolboxCategory.Maintenance,
                ControlKind = ToolboxControlKind.Action,
                TitleZh = "恢复工具箱默认配置",
                TitleEn = "Reset toolbox defaults",
                DescriptionZh = "将工具箱中的所有功能与修复开关恢复为默认启用状态",
                DescriptionEn = "Restore all toolbox features and fixes to their default enabled state",
                ActionTextZh = "恢复默认",
                ActionTextEn = "Reset",
                ActionHandler = AdaptiveSettingsStore.ResetAllToDefaults
            },
            new()
            {
                Id = ToolboxModuleIds.ActionUninstall,
                Category = ToolboxCategory.Maintenance,
                ControlKind = ToolboxControlKind.Action,
                TitleZh = "卸载补丁并恢复官方原版",
                TitleEn = "Uninstall patch & restore official",
                DescriptionZh = "彻底移除 Magic 视界工具箱补丁文件、还原触控板/启动配置并自动重启顶部栏与控制中心",
                DescriptionEn = "Remove toolbox patch files, restore original configs and touchpad defaults, and restart TopBar & Control Center",
                ActionTextZh = "卸载并还原",
                ActionTextEn = "Uninstall",
                IsDestructiveAction = true,
                ActionHandler = UninstallAndRestoreOfficial
            }
        };

        internal static string? ResolveRestoreScriptSourcePath()
        {
            string baseDir = AppContext.BaseDirectory;
            string deployed = Path.Combine(baseDir, "restore_adaptive_topbar.ps1");
            if (File.Exists(deployed))
            {
                return deployed;
            }

            string adjacent = Path.Combine(baseDir, "restore_topbar.ps1");
            return File.Exists(adjacent) ? adjacent : null;
        }

        internal static string BuildFallbackUninstallScript()
        {
            return @"
param([switch]$Elevated, [switch]$SelfDelete)
$ErrorActionPreference = 'SilentlyContinue'
$appDir = 'C:\Program Files\HONOR\MagicAnimation\MagicTaskbarApp'
$dstDll = Join-Path $appDir 'TopBarAdaptiveColor.dll'
$dstRestore = Join-Path $appDir 'restore_adaptive_topbar.ps1'
$rcPath = Join-Path $appDir 'magictaskbar-ui.runtimeconfig.json'
$bakPath = Join-Path $appDir 'magictaskbar-ui.runtimeconfig.json.bak'
if (Test-Path $bakPath) {
    Copy-Item $bakPath $rcPath -Force
    Remove-Item $bakPath -Force
} elseif (Test-Path $rcPath) {
    $json = Get-Content $rcPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -ne $json.runtimeOptions.configProperties -and $json.runtimeOptions.configProperties.PSObject.Properties.Name -contains 'STARTUP_HOOKS') {
        $json.runtimeOptions.configProperties.PSObject.Properties.Remove('STARTUP_HOOKS')
        [System.IO.File]::WriteAllText($rcPath, ($json | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))
    }
}
Get-Process -Name 'magictaskbar-ui' | Stop-Process -Force
Start-Sleep -Seconds 2
Remove-Item $dstDll, $dstRestore -Force
Remove-Item (Join-Path $env:APPDATA 'taskbarAndAIbar\adaptive-topbar.json') -Force
Remove-Item (Join-Path $env:APPDATA 'taskbarAndAIbar\pinned-tray-icons.json') -Force
Remove-Item (Join-Path $env:TEMP 'MagicTaskbar\adaptive-icons') -Recurse -Force
Remove-Item (Join-Path $env:TEMP 'MagicTaskbar\adaptive-topbar.log') -Force
$ccDir = 'C:\Program Files\HONOR\NotifControlUI'
$ccExe = Join-Path $ccDir 'ControlCenterTrayUI.exe'
$dstBoostDll = Join-Path $ccDir 'ControlCenterSpeedBoost.dll'
$dstBoostCfg = Join-Path $ccDir 'ControlCenterTrayUI.exe.config'
$bakBoostCfg = Join-Path $ccDir 'ControlCenterTrayUI.exe.config.bak'
Get-Process -Name 'ControlCenterTrayUI' | Stop-Process -Force
Start-Sleep -Seconds 1
if (Test-Path $bakBoostCfg) {
    Copy-Item $bakBoostCfg $dstBoostCfg -Force
    Remove-Item $bakBoostCfg -Force
} elseif (Test-Path $dstBoostCfg) {
    Remove-Item $dstBoostCfg -Force
}
Remove-Item $dstBoostDll -Force
Remove-Item (Join-Path $env:TEMP 'MagicTaskbar\control-center-boost.log') -Force
$tpReg = 'HKCU:\SOFTWARE\PCManager\TouchPadSetting'
if (Test-Path $tpReg) {
    Set-ItemProperty -Path $tpReg -Name 'TripleFingerLightTouch' -Value 1 -Type DWord
    $g = (Get-ItemProperty -Path $tpReg -Name 'TouchpadGestures').TouchpadGestures
    if ($g -is [string]) {
        $parts = @($g.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
        if ($parts -notcontains '27') {
            $parts += '27'
            Set-ItemProperty -Path $tpReg -Name 'TouchpadGestures' -Value ($parts -join ',') -Type String
        }
    }
}
$exePath = Join-Path $appDir 'magictaskbar-ui.exe'
if (Test-Path $exePath) { Start-Process 'explorer.exe' -ArgumentList ('""' + $exePath + '""') }
if (Test-Path $ccExe) { Start-Sleep -Seconds 1; Start-Process 'explorer.exe' -ArgumentList ('""' + $ccExe + '""') }
if ($SelfDelete -and (Test-Path $PSCommandPath)) { Remove-Item $PSCommandPath -Force }
";
        }

        private static void UninstallAndRestoreOfficial()
        {
            bool isZh = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            string prompt = isZh
                ? "确定要卸载「Magic 视界工具箱」补丁并彻底恢复官方原版状态吗？\n\n确认后将请求管理员权限，清理全部补丁文件、还原触控板默认手势，并自动重启顶部栏与控制中心。"
                : "Uninstall Magic Vision Toolbox patch and restore HONOR's original files?\n\nAdministrator permission (UAC) will be requested to clean up patch files, restore defaults, and restart TopBar & Control Center.";
            string caption = isZh ? "卸载并恢复官方原版" : "Uninstall Magic Toolbox";

            if (MessageBox.Show(prompt, caption, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            string tempScript = Path.Combine(
                Path.GetTempPath(),
                $"uninstall_magic_toolbox_{Environment.ProcessId}.ps1");
            try
            {
                string? sourceScript = ResolveRestoreScriptSourcePath();
                if (!string.IsNullOrEmpty(sourceScript))
                {
                    File.Copy(sourceScript, tempScript, overwrite: true);
                }
                else
                {
                    File.WriteAllText(tempScript, BuildFallbackUninstallScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                }

                DiagnosticLog.Info($"Launching elevated uninstall script from UI: {tempScript}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\" -Elevated -SelfDelete",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                DiagnosticLog.Info("UI uninstall cancelled at UAC prompt.");
                TryDeleteFile(tempScript);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"UninstallAndRestoreOfficial failed: {ex.Message}");
                TryDeleteFile(tempScript);
                string errText = isZh
                    ? $"启动卸载程序失败：{ex.Message}"
                    : $"Failed to launch uninstaller: {ex.Message}";
                MessageBox.Show(errText, caption, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void OpenDiagnosticLogFolder()
        {
            try
            {
                string logDir = Path.Combine(Path.GetTempPath(), "MagicTaskbar");
                Directory.CreateDirectory(logDir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = logDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"OpenDiagnosticLogFolder failed: {ex.Message}");
            }
        }
    }
}

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
        public const string NotificationAlertAnimation = "notification_alert_animation";
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
        public const string ActionOpenGitHub = "action_open_github";
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
        public const string GitHubRepoUrl = "https://github.com/spkyle/HonorMagicVisionToolbox";

        public static readonly IReadOnlyList<ToolboxCategoryMeta> Categories = new ToolboxCategoryMeta[]
        {
            new()
            {
                Category = ToolboxCategory.StatusBarEnhancements,
                TitleZh = "一、顶部状态栏增强",
                TitleEn = "Top Status Bar Enhancements"
            },
            new()
            {
                Category = ToolboxCategory.DockEnhancements,
                TitleZh = "二、底部 Dock 多任务增强",
                TitleEn = "Taskbar & Multitasking"
            },
            new()
            {
                Category = ToolboxCategory.BugFixes,
                TitleZh = "三、问题修复与体验优化",
                TitleEn = "Fixes & Optimizations"
            },
            new()
            {
                Category = ToolboxCategory.Maintenance,
                TitleZh = "四、项目主页与维护工具",
                TitleEn = "Project & Maintenance"
            }
        };

        public static readonly IReadOnlyList<ToolboxModuleDescriptor> Modules = new ToolboxModuleDescriptor[]
        {
            new()
            {
                Id = ToolboxModuleIds.AdaptiveTopBarColor,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "全屏 / 最大化时状态栏自适应颜色",
                TitleEn = "Adaptive top bar color for maximized windows",
                DescriptionZh = "解决最大化后顶部始终存在透明桌面条的问题，自动匹配当前窗口标题栏颜色并自适应深浅文字与图标",
                DescriptionEn = "Match maximized window header color and adapt light/dark foreground in fixed mode",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.ShowYoyoClawButton,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "显示 / 隐藏右侧 YOYO Claw 按钮",
                TitleEn = "Show / hide YOYO Claw button on top bar",
                DescriptionZh = "控制状态栏右侧控制中心旁默认无法关闭的 YOYO Claw 按钮显示，关闭后立即隐藏",
                DescriptionEn = "Show or hide the YOYO Claw button beside Control Center on the top bar",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.NotificationAlertAnimation,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "有通知时右上角按钮变色与约 2 秒轻震动提醒",
                TitleEn = "Highlight & gently shake Notification button (~2s) when unread alerts exist",
                DescriptionZh = "有系统通知或微信等顶栏应用图标闪烁未读时，自动为右上角通知按钮切换醒目配色并约每 2 秒轻抖一次；点开或已读后恢复常态",
                DescriptionEn = "Use a distinct highlight color and gentle ~2s bell shake on NotificationButton while unread notifications or flashing tray alerts exist",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.HiddenTrayIconPinning,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "支持拖拽任意系统隐藏托盘图标固定到顶部栏",
                TitleEn = "Drag any hidden tray icon to pin on top bar",
                DescriptionZh = "突破原版仅支持飞书、钉钉、微信等少数应用的限制，展开 ^ 托盘后任意程序拖到顶栏即可固定，也可直接拖回 ^ 箭头上收起",
                DescriptionEn = "Drag any icon from the overflow tray onto the top bar to pin it; drag bar icons directly onto the ^ arrow (or downward) to hide back into overflow",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.NativeTrayRightClick,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "顶部栏通知图标右键弹出程序原生菜单",
                TitleEn = "Forward right-click on top bar app icons to native tray menu",
                DescriptionZh = "解决原版固定图标右键只有「从顶部栏移除」的问题，开启后可直接原地弹出对应程序的系统托盘原生功能菜单（按住 Shift + 右键仍可移除）",
                DescriptionEn = "Relay right-clicks on fixed app notification icons to their Windows tray menu (hold Shift+RightClick for Remove from Top Bar)",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.TopBarMaximizedWindowDrag,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "顶栏空白处拖拽 & 双击控制已最大化窗口",
                TitleEn = "Drag or double-click blank top bar to restore & move maximized window",
                DescriptionZh = "配合自适应状态栏，在顶部空白处按住向任意方向拖过阈值即可还原并跟手移动（无需再去下面找程序标题栏），双击空白处可直接还原或最大化",
                DescriptionEn = "Hold left-click in the blank top bar strip and drag >6px in any direction to restore and drag the maximized window, or double-click to toggle restore",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.DockPreviewKeepOpenOnClose,
                Category = ToolboxCategory.DockEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "中键 / 点 × 关闭单窗口后保持多窗口缩略图展开",
                TitleEn = "Keep thumbnail strip open after middle-closing one window",
                DescriptionZh = "解决关闭一个窗口就把整个预览堆叠全部关掉的问题，自动移除已关闭卡片并保持其余缩略图展开，方便连续关闭多个堆叠窗口",
                DescriptionEn = "Remove only the closed window card on middle-click/close and keep remaining stacked thumbnails open",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.DockPreviewAeroPeek,
                Category = ToolboxCategory.DockEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "对齐 Windows 二级预览：悬停缩略图卡片时桌面实时预览 (Aero Peek)",
                TitleEn = "Live desktop Aero Peek when hovering thumbnail cards",
                DescriptionZh = "补充原版缺少的二级预览能力，悬停卡片时还原并高亮指定窗口（清晰区分多个 cmd 等同类窗口），鼠标移开自动收回",
                DescriptionEn = "Preview the hovered window full-size on the desktop while keeping thumbnails on top; restore on leave",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.DockPreviewTransferCorridor,
                Category = ToolboxCategory.DockEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "Dock 多窗口预览斜向防误关走廊",
                TitleEn = "Protect Dock multi-window preview when moving diagonally across divider or icons",
                DescriptionZh = "优化鼠标从底部 Dock 滑动切换多任务时的范围判定，斜向移向缩略图经过分隔线或擦碰相邻图标时不再突然收起预览",
                DescriptionEn = "Prevent accidental preview dismissal or premature switching when crossing the separator or brushing adjacent Dock icons toward preview cards",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.TopBarPopupEdgeClamp,
                Category = ToolboxCategory.BugFixes,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "修复右侧控制中心 / 电池 / 音量弹窗超出屏幕被裁切",
                TitleEn = "Clamp top bar popups inside monitor bounds",
                DescriptionZh = "隐藏部分右侧按钮导致弹窗右移时，自动将其与亚克力背景层限制在当前显示器安全工作区内",
                DescriptionEn = "Prevent battery/volume/network popups and acrylic backdrops from clipping past the screen edge",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.AdaptiveTopBarGapFix,
                Category = ToolboxCategory.BugFixes,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "修复窗口最大化后状态栏下方露出一线壁纸细缝",
                DescriptionZh = "工作区重算导致最大化窗口下移产生细缝时，自动微调状态栏底部向下延展封住壁纸缝隙",
                TitleEn = "Fix top bar bottom seam after maximizing",
                DescriptionEn = "Extend top bar bottom edge when workspace reservation shifts windows down",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.FastSettingsResponse,
                Category = ToolboxCategory.BugFixes,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "控制中心与设置点击响应速度优化",
                TitleEn = "Boost Control Center & Settings click responsiveness",
                DescriptionZh = "按下即响应并维持热驻留与预热（除冷启动首次加载约 <3 秒外，日常打开几乎无感），并消除关闭闪烁",
                DescriptionEn = "Trigger on press, keep Control Center & Settings warm in memory, and defer heavy pre-show scans",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.FastNotificationResponse,
                Category = ToolboxCategory.BugFixes,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "顶部通知按钮弹出速度优化并修复假死",
                TitleEn = "Boost top bar Notification button response & auto-recover",
                DescriptionZh = "按下即直连唤醒已运行通知中心大幅提速，同时修复高频点击卡死与假死问题，异常时自动重建恢复",
                DescriptionEn = "Trigger on press, wake existing MessageCenter directly without respawning, and auto-recover if stuck",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.TouchpadThreeFingerSearch,
                Category = ToolboxCategory.BugFixes,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "压力触控板三指轻触「智慧搜索」独立开关",
                TitleEn = "Allow three-finger touchpad light touch to open AI Search",
                DescriptionZh = "关闭后可单独禁用移动办公时极易误触的三指轻触「智慧搜索」，完整保留三指拖拽与其余手势",
                DescriptionEn = "Turn off to disable three-finger Force Touch opening HONOR AI Search while keeping all other gestures intact",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.ActionOpenGitHub,
                Category = ToolboxCategory.Maintenance,
                ControlKind = ToolboxControlKind.Action,
                TitleZh = "GitHub 开源仓库",
                TitleEn = "GitHub Repository",
                DescriptionZh = GitHubRepoUrl,
                DescriptionEn = GitHubRepoUrl,
                ActionTextZh = "打开仓库",
                ActionTextEn = "Open Repo",
                ActionHandler = OpenGitHubRepository
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

        private static void OpenGitHubRepository()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = GitHubRepoUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn($"OpenGitHubRepository failed: {ex.Message}");
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

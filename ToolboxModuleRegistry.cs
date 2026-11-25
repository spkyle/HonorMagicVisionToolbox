using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;

namespace TopBarAdaptiveColor
{
    internal enum ToolboxCategory
    {
        Desktop = 0,
        StatusBarEnhancements = 1,
        DockEnhancements = 2,
        BugFixes = 3,
        Maintenance = 4
    }

    internal enum ToolboxControlKind
    {
        Toggle = 0,
        Action = 1
    }

    internal static class ToolboxModuleIds
    {
        public const string NativeWindowsDesktop = "native_windows_desktop";
        public const string DisableTopBar = "disable_topbar";
        public const string DisableDock = "disable_dock";
        public const string AdaptiveTopBarColor = "adaptive_topbar_color";
        public const string ShowYoyoClawButton = "show_yoyo_claw_button";
        public const string NotificationAlertAnimation = "notification_alert_animation";
        public const string NativeTrayRightClick = "native_tray_right_click";
        public const string HiddenTrayIconPinning = "hidden_tray_icon_pinning";
        public const string TopBarMaximizedWindowDrag = "topbar_maximized_window_drag";
        public const string DockStartNativeMenu = "dock_start_native_menu";
        public const string DockHighRefreshRate = "dock_high_refresh_rate";
        public const string DockPreviewKeepOpenOnClose = "dock_preview_keep_open_on_close";
        public const string DockPreviewAeroPeek = "dock_preview_aero_peek";
        public const string DockPreviewTransferCorridor = "dock_preview_transfer_corridor";
        public const string AdaptiveTopBarGapFix = "adaptive_topbar_gap_fix";
        public const string TopBarPopupEdgeClamp = "topbar_popup_edge_clamp";
        public const string FastSettingsResponse = "fast_settings_response";
        public const string FastNotificationResponse = "fast_notification_response";
        public const string FastOpenSoftwareRendering = "fast_open_software_rendering";
        public const string FastOpenIdleMemoryTrim = "fast_open_idle_memory_trim";
        public const string FastDesktopRightClick = "fast_desktop_right_click";
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
        public IReadOnlyList<string>? ParentModuleIds { get; init; }
        public IReadOnlyList<string>? DisabledWhenModuleIds { get; init; }
        public string? ActionTextZh { get; init; }
        public string? ActionTextEn { get; init; }
        public bool IsDestructiveAction { get; init; }
        public Action? ActionHandler { get; init; }
    }

    internal static class ToolboxModuleRegistry
    {
        public const string FallbackVersion = "1.0.2";
        public static readonly string ToolboxVersion = ResolveToolboxVersion();
        public const string GitHubRepoUrl = "https://github.com/spkyle/HonorMagicVisionToolbox";

        private static string ResolveToolboxVersion()
        {
            try
            {
                string? info = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    int plusIdx = info.IndexOf('+');
                    string clean = (plusIdx >= 0 ? info.Substring(0, plusIdx) : info).Trim();
                    if (!string.IsNullOrEmpty(clean))
                    {
                        return clean.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                            ? "v" + clean.Substring(1)
                            : "v" + clean;
                    }
                }
            }
            catch
            {
            }

            return "v" + FallbackVersion;
        }

        public static readonly IReadOnlyList<ToolboxCategoryMeta> Categories = new ToolboxCategoryMeta[]
        {
            new()
            {
                Category = ToolboxCategory.Desktop,
                TitleZh = "一、桌面",
                TitleEn = "1. Desktop"
            },
            new()
            {
                Category = ToolboxCategory.StatusBarEnhancements,
                TitleZh = "二、顶部状态栏",
                TitleEn = "2. Top Status Bar"
            },
            new()
            {
                Category = ToolboxCategory.DockEnhancements,
                TitleZh = "三、底部 Dock 栏",
                TitleEn = "3. Bottom Dock"
            },
            new()
            {
                Category = ToolboxCategory.BugFixes,
                TitleZh = "四、通用性能与系统",
                TitleEn = "4. System & Performance"
            },
            new()
            {
                Category = ToolboxCategory.Maintenance,
                TitleZh = "五、项目与维护",
                TitleEn = "5. Project & Maintenance"
            }
        };

        public static readonly IReadOnlyList<ToolboxModuleDescriptor> Modules = new ToolboxModuleDescriptor[]
        {
            // === 一、桌面 ===
            new()
            {
                Id = ToolboxModuleIds.NativeWindowsDesktop,
                Category = ToolboxCategory.Desktop,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "关闭荣耀桌面接管（还原 Windows 原生桌面）",
                TitleEn = "Disable HONOR desktop takeover (restore native Windows desktop)",
                DescriptionZh = "优雅退出荣耀桌面覆盖层（DesktopHost），恢复 100% 原生 Windows 右键菜单、左键高刷框选与图标拖拽；关闭开关可随时恢复荣耀桌面",
                DescriptionEn = "Gracefully exit DesktopHost to restore native Windows right-click, high-FPS drag-select, and icon drag-drop (turn off anytime to restore HONOR desktop)",
                DefaultEnabled = false
            },
            new()
            {
                Id = ToolboxModuleIds.FastDesktopRightClick,
                Category = ToolboxCategory.Desktop,
                ControlKind = ToolboxControlKind.Toggle,
                DisabledWhenModuleIds = new[]
                {
                    ToolboxModuleIds.NativeWindowsDesktop
                },
                TitleZh = "桌面右键加速与防假死预热",
                TitleEn = "Desktop right-click acceleration & MenuHost prewarm",
                DescriptionZh = "二级选项（保留荣耀桌面时生效）：解除桌面右键宿主进程的 Win11 效能模式限频与内存降级、进程自回收后自动以普通权限预热，并在右键按下时预聚焦桌面覆盖层",
                DescriptionEn = "Sub-option (when keeping HONOR desktop): disable Win11 EcoQoS throttling on DesktopHost & MenuHostNative, auto-prewarm MenuHostNative at Medium IL after self-recycle, and pre-focus on right-button down",
                DefaultEnabled = true
            },

            // === 二、顶部状态栏 ===
            new()
            {
                Id = ToolboxModuleIds.DisableTopBar,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "关闭顶部状态栏",
                TitleEn = "Disable top status bar",
                DescriptionZh = "关闭荣耀顶部状态栏并释放顶部工作区，最大化窗口占满屏幕顶部；关闭期间将在系统托盘保留「Magic 视界工具箱」图标，点击可随时打开设置或恢复顶栏",
                DescriptionEn = "Close the HONOR top bar and release reserved top workspace; keeps a Magic Toolbox icon in the system tray so you can reopen settings or restore TopBar anytime",
                DefaultEnabled = false
            },
            new()
            {
                Id = ToolboxModuleIds.AdaptiveTopBarColor,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                DisabledWhenModuleIds = new[]
                {
                    ToolboxModuleIds.DisableTopBar
                },
                TitleZh = "全屏与最大化时状态栏自适应配色",
                TitleEn = "Adaptive top bar color for maximized windows",
                DescriptionZh = "匹配当前最大化窗口标题栏颜色并自适应深浅前景，同时避免全屏录屏浮层导致顶栏误隐藏",
                DescriptionEn = "Match maximized window header color, adapt light/dark foreground, and avoid false fullscreen hides on recording overlays",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.AdaptiveTopBarGapFix,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                DisabledWhenModuleIds = new[]
                {
                    ToolboxModuleIds.DisableTopBar
                },
                TitleZh = "修正窗口最大化后顶栏下方缝隙",
                TitleEn = "Fix top bar bottom seam after maximizing",
                DescriptionZh = "工作区重算导致最大化窗口轻微下移时，向下扩展状态栏背景高度以消除桌面透缝",
                DescriptionEn = "Extend top bar bottom edge when workspace reservation shifts windows down",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.TopBarPopupEdgeClamp,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                DisabledWhenModuleIds = new[]
                {
                    ToolboxModuleIds.DisableTopBar
                },
                TitleZh = "修正右侧弹出面板超出屏幕边缘",
                TitleEn = "Clamp top bar popups inside monitor bounds",
                DescriptionZh = "隐藏部分右侧图标后，将控制中心、电池、音量等弹窗及其模糊层限制在当前显示器工作区内",
                DescriptionEn = "Prevent battery/volume/network popups and acrylic backdrops from clipping past the screen edge",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.ShowYoyoClawButton,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                DisabledWhenModuleIds = new[]
                {
                    ToolboxModuleIds.DisableTopBar
                },
                TitleZh = "显示右侧 YOYO Claw 按钮",
                TitleEn = "Show YOYO Claw button on top bar",
                DescriptionZh = "控制状态栏右侧控制中心旁 YOYO Claw 按钮的显示状态，取消勾选后立即隐藏",
                DescriptionEn = "Show or hide the YOYO Claw button beside Control Center on the top bar",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.NotificationAlertAnimation,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                DisabledWhenModuleIds = new[]
                {
                    ToolboxModuleIds.DisableTopBar
                },
                TitleZh = "微信等应用未读消息收纳至通知中心与红标震动提醒",
                TitleEn = "Bridge unread chat app alerts to Notification Center with red bell & shake",
                DescriptionZh = "将微信、飞书、钉钉、QQ 等顶栏未读消息同步生成卡片收纳到右上角通知中心，通知按钮高亮红色 (#ED3D37) 并约每 2 秒震动提醒；点卡可跳转原窗口",
                DescriptionEn = "Mirror flashing top-bar app alerts (WeChat, Feishu, DingTalk, QQ) into Notification Center cards, highlight the bell button red (#ED3D37) with a ~2s shake, and focus the app window on click",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.HiddenTrayIconPinning,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                DisabledWhenModuleIds = new[]
                {
                    ToolboxModuleIds.DisableTopBar
                },
                TitleZh = "任意隐藏托盘图标拖拽固定与拖回",
                TitleEn = "Drag any hidden tray icon to pin or hide on top bar",
                DescriptionZh = "展开 ^ 隐藏托盘后可将任意程序图标拖到顶栏固定；把顶栏图标拖回 ^ 箭头松手即可收进隐藏托盘",
                DescriptionEn = "Drag any icon from the overflow tray onto the top bar to pin it; drag bar icons onto the ^ arrow (or downward) to hide back into overflow",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.NativeTrayRightClick,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                DisabledWhenModuleIds = new[]
                {
                    ToolboxModuleIds.DisableTopBar
                },
                TitleZh = "顶栏应用图标右键弹出原生托盘菜单",
                TitleEn = "Forward right-click on top bar app icons to native tray menu",
                DescriptionZh = "转发顶栏应用图标的右键点击至原程序托盘菜单（按住 Shift + 右键仍可从顶栏移除）",
                DescriptionEn = "Relay right-clicks on fixed app notification icons to their Windows tray menu (hold Shift+RightClick for Remove from Top Bar)",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.TopBarMaximizedWindowDrag,
                Category = ToolboxCategory.StatusBarEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                DisabledWhenModuleIds = new[]
                {
                    ToolboxModuleIds.DisableTopBar
                },
                TitleZh = "顶栏空白处拖拽还原与双击控制",
                TitleEn = "Drag or double-click blank top bar to restore & move maximized window",
                DescriptionZh = "在顶栏空白处按住左键拖动超过 6px 即可还原并移动当前最大化窗口，双击空白处可切换最大化状态",
                DescriptionEn = "Hold left-click in the blank top bar strip and drag >6px in any direction to restore and move the maximized window, or double-click to toggle maximize",
                DefaultEnabled = true
            },

            // === 三、底部 Dock 栏 ===
            new()
            {
                Id = ToolboxModuleIds.DisableDock,
                Category = ToolboxCategory.DockEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "关闭底部 Dock 栏（还原 Windows 原生任务栏）",
                TitleEn = "Disable bottom Dock (restore native Windows taskbar)",
                DescriptionZh = "关闭荣耀底部 Dock 栏、释放底部工作区并立即恢复 Windows 原生任务栏；关闭开关可随时恢复荣耀底栏",
                DescriptionEn = "Close the HONOR bottom Dock, release reserved bottom workspace, and restore the native Windows taskbar (turn off anytime to restore HONOR Dock)",
                DefaultEnabled = false
            },
            new()
            {
                Id = ToolboxModuleIds.DockStartNativeMenu,
                Category = ToolboxCategory.DockEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                DisabledWhenModuleIds = new[]
                {
                    ToolboxModuleIds.DisableDock
                },
                TitleZh = "底栏开始按钮右键弹出原生 Windows 菜单",
                TitleEn = "Right-click Dock Start button for native Windows menu (Win+X)",
                DescriptionZh = "右键点击底部 Dock 栏的 Windows 开始按钮时，弹出原生 Windows 快捷菜单（Win + X，含任务管理器、终端、设备管理器、电源选项等）",
                DescriptionEn = "Right-click the Windows Start button on the bottom Dock to open the native Windows Quick Link menu (Win + X)",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.DockHighRefreshRate,
                Category = ToolboxCategory.DockEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                DisabledWhenModuleIds = new[]
                {
                    ToolboxModuleIds.DisableDock
                },
                TitleZh = "底栏原生高刷解锁（同步显示器 VSync）",
                TitleEn = "Unlock native high-refresh-rate Dock animations (DWM VSync)",
                DescriptionZh = "突破 WPF 60Hz 上限与鼠标 15ms 输入节流，动效期间按显示器硬件垂直同步（DwmFlush）直驱 DirectComposition，解锁 120Hz/165Hz 满帧波浪缩放与滑入滑出（闲置时 0% 占用）",
                DescriptionEn = "Bypass WPF's 60Hz cap and 15ms hover throttle by pacing DirectComposition motion frames to hardware DwmFlush VSync (120Hz/165Hz+ smoothness, 0% idle CPU)",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.DockPreviewKeepOpenOnClose,
                Category = ToolboxCategory.DockEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                DisabledWhenModuleIds = new[]
                {
                    ToolboxModuleIds.DisableDock
                },
                TitleZh = "关闭单个窗口后保持多窗口缩略图展开",
                TitleEn = "Keep thumbnail strip open after middle-closing one window",
                DescriptionZh = "中键或点击关闭缩略图中的单个窗口时，仅移除该窗口卡片并刷新排版，不收起整组预览",
                DescriptionEn = "Remove only the closed window card on middle-click/close and keep remaining stacked thumbnails open",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.DockPreviewAeroPeek,
                Category = ToolboxCategory.DockEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                DisabledWhenModuleIds = new[]
                {
                    ToolboxModuleIds.DisableDock
                },
                TitleZh = "悬停缩略图卡片实时桌面预览 (Aero Peek)",
                TitleEn = "Live desktop Aero Peek when hovering thumbnail cards",
                DescriptionZh = "鼠标悬停在 Dock 缩略图卡片上时在桌面临时显示对应窗口完整画面，移开后恢复原状态",
                DescriptionEn = "Preview the hovered window full-size on the desktop while keeping thumbnails on top; restore on leave",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.DockPreviewTransferCorridor,
                Category = ToolboxCategory.DockEnhancements,
                ControlKind = ToolboxControlKind.Toggle,
                DisabledWhenModuleIds = new[]
                {
                    ToolboxModuleIds.DisableDock
                },
                TitleZh = "Dock 缩略图斜向移动防误关",
                TitleEn = "Protect Dock multi-window preview when moving diagonally across divider or icons",
                DescriptionZh = "鼠标从底部 Dock 图标斜向移至预览缩略图期间，经过分隔线或擦过相邻图标时不中断当前预览",
                DescriptionEn = "Prevent accidental preview dismissal or premature switching when crossing the separator or brushing adjacent Dock icons toward preview cards",
                DefaultEnabled = true
            },

            // === 四、通用性能与系统 ===
            new()
            {
                Id = ToolboxModuleIds.FastSettingsResponse,
                Category = ToolboxCategory.BugFixes,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "控制中心与设置快速响应与再点收起",
                TitleEn = "Fast response & click-again dismiss for Control Center and Settings",
                DescriptionZh = "按钮按下即触发、控制中心与设置窗口驻留预热并消除关闭闪烁，再次点击右上角控制中心图标可收起面板",
                DescriptionEn = "Trigger on press, keep Control Center & Settings warm in memory, sync hide animation, and dismiss when clicking the button again",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.FastNotificationResponse,
                Category = ToolboxCategory.BugFixes,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "通知按钮快速响应、假死恢复与再点收起",
                TitleEn = "Fast response, auto-recover & click-again dismiss for Notification button",
                DescriptionZh = "直接向通知中心进程发送唤醒消息减少延迟，进程无响应时自动重建；再次点击图标可收起面板",
                DescriptionEn = "Trigger on press, wake existing MessageCenter directly, auto-recover if stuck, and dismiss when clicking the button again",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.FastOpenSoftwareRendering,
                Category = ToolboxCategory.BugFixes,
                ControlKind = ToolboxControlKind.Toggle,
                ParentModuleIds = new[]
                {
                    ToolboxModuleIds.FastSettingsResponse,
                    ToolboxModuleIds.FastNotificationResponse
                },
                TitleZh = "秒开省内存 · 软件渲染降占",
                TitleEn = "Fast-open memory saver · Software rendering",
                DescriptionZh = "二级开关：控制中心与通知中心强制使用 WPF 软件渲染以减少约 130+ MB 渲染缓冲；默认开启，可单独关闭以恢复 GPU 硬件加速，或与下方闲置释放叠加（切换后重启对应弹窗生效）",
                DescriptionEn = "Sub-option: force WPF software rendering for Control Center & Notification Center to cut ~130+ MB GPU buffer footprint; enabled by default (turn off to restore GPU acceleration; applies on popup restart)",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.FastOpenIdleMemoryTrim,
                Category = ToolboxCategory.BugFixes,
                ControlKind = ToolboxControlKind.Toggle,
                ParentModuleIds = new[]
                {
                    ToolboxModuleIds.FastSettingsResponse,
                    ToolboxModuleIds.FastNotificationResponse
                },
                TitleZh = "秒开省内存 · 闲置自动释放工作集",
                TitleEn = "Fast-open memory saver · Release idle memory",
                DescriptionZh = "二级开关：解除顶栏裁剪锁并允许回收托管垃圾，弹窗隐藏闲置 3 分钟后压缩工作集大幅降低常驻占用；默认开启，久置首开可能有短暂微顿，可单独关闭以保持热内存常驻",
                DescriptionEn = "Sub-option: allow TopBar trim & GC compaction, and softly trim popup working set after 3m idle; enabled by default (turn off to stay warm in RAM)",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.TouchpadThreeFingerSearch,
                Category = ToolboxCategory.BugFixes,
                ControlKind = ToolboxControlKind.Toggle,
                TitleZh = "允许压力触控板三指轻触打开智慧搜索",
                TitleEn = "Allow three-finger touchpad light touch to open AI Search",
                DescriptionZh = "取消勾选后单独关闭三指轻触智慧搜索（同步更新注册表与触控板 HID 配置），其余多指手势保持不变",
                DescriptionEn = "Turn off to disable three-finger Force Touch opening HONOR AI Search while keeping all other gestures intact",
                DefaultEnabled = true
            },
            new()
            {
                Id = ToolboxModuleIds.ActionOpenGitHub,
                Category = ToolboxCategory.Maintenance,
                ControlKind = ToolboxControlKind.Action,
                TitleZh = "项目仓库 (GitHub)",
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
                TitleZh = "诊断日志",
                TitleEn = "Diagnostic logs",
                DescriptionZh = "打开运行日志目录 (%TEMP%\\MagicTaskbar)",
                DescriptionEn = "Open the diagnostic log folder (%TEMP%\\MagicTaskbar)",
                ActionTextZh = "打开目录",
                ActionTextEn = "Open Folder",
                ActionHandler = OpenDiagnosticLogFolder
            },
            new()
            {
                Id = ToolboxModuleIds.ActionResetDefaults,
                Category = ToolboxCategory.Maintenance,
                ControlKind = ToolboxControlKind.Action,
                TitleZh = "恢复默认设置",
                TitleEn = "Reset defaults",
                DescriptionZh = "将所有功能开关重置为默认启用状态",
                DescriptionEn = "Restore all switches to their default enabled state",
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
                DescriptionZh = "移除全部补丁文件、恢复启动与触控板默认配置，并重启顶部栏与控制中心",
                DescriptionEn = "Remove patch files, restore startup and touchpad defaults, and restart TopBar & Control Center",
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
$mcDir = 'C:\Program Files\HONOR\PCManager'
$dstBridgeDll = Join-Path $mcDir 'MessageCenterBridge.dll'
$dstBridgeCfg = Join-Path $mcDir 'MessageCenterUI.exe.config'
$bakBridgeCfg = Join-Path $mcDir 'MessageCenterUI.exe.config.bak'
Get-Process -Name 'MessageCenterUI' | Stop-Process -Force
Start-Sleep -Seconds 1
if (Test-Path $bakBridgeCfg) {
    Copy-Item $bakBridgeCfg $dstBridgeCfg -Force
    Remove-Item $bakBridgeCfg -Force
} elseif (Test-Path $dstBridgeCfg) {
    try {
        [xml]$mcXml = Get-Content $dstBridgeCfg -Raw -Encoding UTF8
        $runtimeNode = $mcXml.SelectSingleNode('/configuration/runtime')
        if ($null -ne $runtimeNode) {
            foreach ($oldNode in @($runtimeNode.SelectNodes('appDomainManagerAssembly | appDomainManagerType'))) {
                [void]$runtimeNode.RemoveChild($oldNode)
            }
            [System.IO.File]::WriteAllText($dstBridgeCfg, $mcXml.OuterXml, (New-Object System.Text.UTF8Encoding($false)))
        }
    } catch {}
}
Remove-Item $dstBridgeDll -Force
Remove-Item (Join-Path $env:TEMP 'MagicTaskbar\bridged-notifications.json') -Force
Remove-Item (Join-Path $env:TEMP 'MagicTaskbar\bridged-notifications-ack.json') -Force
Remove-Item (Join-Path $env:TEMP 'MagicTaskbar\message-center-bridge.log') -Force
Remove-Item (Join-Path $env:TEMP 'MagicTaskbar\bridged-icons') -Recurse -Force
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
$dhExe = 'C:\Program Files\HONOR\MagicAnimation\NewExplorer\DesktopHost.exe'
if ((Get-Process -Name 'MagicVisuals' -ErrorAction SilentlyContinue) -and -not (Get-Process -Name 'DesktopHost' -ErrorAction SilentlyContinue) -and (Test-Path $dhExe)) { Start-Process 'explorer.exe' -ArgumentList ('""' + $dhExe + '""') }
if (Test-Path $ccExe) { Start-Sleep -Seconds 1; Start-Process 'explorer.exe' -ArgumentList ('""' + $ccExe + '""') }
if ($SelfDelete -and (Test-Path $PSCommandPath)) { Remove-Item $PSCommandPath -Force }
";
        }

        private static void UninstallAndRestoreOfficial()
        {
            bool isZh = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            string prompt = isZh
                ? "确认卸载 Magic 视界工具箱并恢复官方原始文件吗？\n\n确认后将请求管理员权限，清理补丁文件、还原触控板设置，并重启顶部栏与控制中心。"
                : "Uninstall Magic Vision Toolbox and restore HONOR's original files?\n\nAdministrator permission (UAC) will be requested to clean up patch files, restore defaults, and restart TopBar & Control Center.";
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

# AGENTS.md — HonorMagicVisionToolbox 开发与交接指南

本文档面向新开会话的 AI 助手与维护者，用于快速理解项目目标、架构分工、硬性约束与排障路径，做到零中断继续开发。

---

## 1. 项目定位

**Magic 视界工具箱（HonorMagicVisionToolbox）** 是针对荣耀笔记本 **Magic 视界 / 电脑管家** 桌面组件（顶部状态栏、底部 Dock、控制中心、通知中心、压力触控板）的运行时外挂增强与问题修复工具（当前版本：`v1.0.2`）。

### 核心原则与硬性约束
1. **零官方二进制修改**：绝不修改、替换或破坏荣耀官方签名的 `.exe` / `.dll`（位于 `C:\Program Files\HONOR\...`）。
   - 顶部栏与 Dock（.NET 6）：通过修改 `magictaskbar-ui.runtimeconfig.json` 的 `STARTUP_HOOKS` 加载 `TopBarAdaptiveColor.dll`。
   - 控制中心与通知中心（.NET Framework 4.8）：通过 `ControlCenterTrayUI.exe.config` 与 `MessageCenterUI.exe.config` 的 `appDomainManagerType` / `appDomainManagerAssembly` 加载外置 DLL。
2. **C# 5 / .NET Framework 4.8 语法限制**：
   - `ControlCenterSpeedBoost/ControlCenterSpeedBoost.cs` 与 `MessageCenterBridge/MessageCenterBridge.cs` 由 `TopBarAdaptiveColor.csproj` 的 MSBuild Target 调用系统 `v4.0.30319\csc.exe`（`/platform:x64`）编译。
   - **这两个文件严禁使用 C# 6+ 语法**：不能用 `$""`、`?.`、`nameof(...)`、`is Type x` 模式匹配、`=>` 表达式体方法等。
   - 必须保留 `[assembly: AssemblyVersion("1.0.0.0")]`，否则 `.exe.config` 中的强/版本绑定会失效。

---

## 2. 三端架构与模块地图

### A. 顶部栏与 Dock 端（`TopBarAdaptiveColor.dll`，.NET 6 WPF）
- **注入与调度**：
  - `StartupHook.cs`：运行时入口 `StartupHook.Initialize()`；根据设置注入环境变量 `MAGICTASKBAR_DISABLE_WORKING_SET_TRIM=1`（当启用快速响应且未开启闲置释放时），并在 WPF 就绪后交由 `TopBarHookCoordinator`。
  - `TopBarHookCoordinator.cs`：按窗口类型分别挂接 `TopBarWindow`、`TopBarSettingsWindow`、`TopBarAcrylicPopupWindow`、`DockPreviewWindow`、`MainWindow`（底栏），并初始化 `NativeDesktopController`、`MagicComponentController` 与 `DesktopRightClickAccelerator`。
- **配置与设置页注入**：
  - `ToolboxModuleRegistry.cs`：集中定义所有开关 ID（`ToolboxModuleIds`）、五大分类（一、桌面 / 二、顶部状态栏 / 三、底部 Dock 栏 / 四、通用性能与系统 / 五、项目与维护）、版本号（`ToolboxVersion`）、默认启用状态、二级依赖（`ParentModuleIds`）与组件禁用联动（`DisabledWhenModuleIds`）。
  - `AdaptiveSettingsStore.cs`：持久化及热监听 `%APPDATA%\taskbarAndAIbar\adaptive-topbar.json`（含 `masterEnabled` 总开关与 `savedStates` 隔离保存）。
  - `TopBarSettingsInjector.cs`：向官方 `TopBarSettingsWindow` 注入「Magic 视界工具箱」设置面板（标题栏版本号徽章 + 总开关、二级缩进联动、`TrySelectToolboxTab` 外部直达与一键卸载入口）。
- **状态栏自适应与弹窗修复**：
  - `AdaptiveTopBarController.cs`、`NativeWindowSampler.cs`、`SvgIconThemer.cs`：最大化/全屏窗口识别、`BitBlt` 采样主色与浅色背景对比度适配（含浅色树 `1500ms` 重同步节流、`[ThreadStatic]` 缓冲复用与录屏浮窗过滤）。
  - `TopBarMaximizedWindowController.cs`：顶栏空白处拖拽还原、双击切换最大化及最大化底部缝隙修补。
  - `TopBarAcrylicPopupClamper.cs`：约束右侧系统弹窗不超出工作区（上边界以 `workDip.Top` 为准，避免下移跳动）。
- **交互增强、隐藏托盘与通知桥接**：
  - `TopBarUiEnhancer.cs`：YOYO Claw 按钮隐藏、控制中心/通知按钮 `ClickMode.Press` 直连唤醒与再点收起、通知红标高亮（`#ED3D37`）与轻抖、`TopBarSettingsFastReuse`（设置页关闭改 `Hide()` 热复用）。
  - `HiddenTrayPinEnhancer.cs`：`^` 隐藏托盘拖拽固定/拖回，及最小化到托盘的应用（如微信 4.x `Weixin.exe`）点击激活协同。
  - `StatusNotificationTrayRelay.cs`：顶栏图标右键转发至原程序原生托盘菜单及托盘点击消息协议。
  - `StatusNotificationBridgeCoordinator.cs`：监控顶栏未读状态变化并写入桥接文件，消费 `ack` 文件（按 `LastWriteTimeUtc` + `Length` 缓存）并触发托盘唤醒。
- **三大组件自由组合、Dock、桌面与触控板**：
  - `NativeDesktopController.cs`：一、桌面主开关（`native_windows_desktop`）——开启后向 `HonorDesktop_HideWnd` 发送 `WM_CLOSE` 优雅退出 `DesktopHost.exe`、校验 `SysListView32` 可见性、清理孤立 `MenuHostNative.exe` 并轻量守护开机/重启竞态；关闭开关或卸载时通过 `explorer.exe` 令牌（`CreateProcessWithTokenW`）以 Medium IL 无损恢复 `DesktopHost.exe`。
  - `MagicComponentController.cs`：二、顶部状态栏主开关（`disable_topbar`）与三、底部 Dock 栏主开关（`disable_dock`）——通过反射驱动 `MagicTaskbarWpf.HostApp` 的 `_launchTopBar`/`_launchDock` 与 `ReconcileTopBarWindows`/`ReconcileDockWindows`，关闭窗口前解绑 `HostApp.Window_Closed` 防止全关时宿主退出，关闭底栏时恢复原生 `Shell_TrayWnd`，关闭顶栏时通过纯 Win32 `Shell_NotifyIconW` 挂载「Magic 视界工具箱」系统托盘入口供随时重开设置或恢复组件。
  - `DockStartRightClickEnhancer.cs`：挂接底栏 `MagicTaskbarWpf.MainWindow` 的 `NativeCompositionDockWindow.ItemContextRequested`（及 WPF 回退事件），右键开始按钮（`utility:start`）时通过 `SendInput` 触发原生 Windows 快捷菜单（`Win + X`）。
  - `DockHighRefreshRateEnhancer.cs`：底栏原生高刷解锁（`dock_high_refresh_rate`）——仅在 `MainWindow._isRenderingActive == true` 期间以 `DwmFlush()` 硬件 VSync 节奏通过编译委托直驱 `TryUpdateCompositionMotionFrame(..., 3.5, inputDriven: true)`，绕开 WPF 60Hz 与鼠标 `15ms` 节流并自动抑制 60Hz 重复步进，解锁 120Hz/165Hz/240Hz DirectComposition 动效（闲置 0% CPU）。
  - `DockPreviewEnhancer.cs`：Dock 缩略图关闭单窗保持展开、`DwmRegisterThumbnail` 悬停预览（Aero Peek）及斜向移动防误关。
  - `DesktopRightClickAccelerator.cs`：桌面右键加速与防假死预热（解除 `DesktopHost` / `MenuHostNative` / `CardMenuHostNative` 的 Win11 EcoQoS 限频与低内存优先级、`MenuHostNative` 自回收退出后以 `DesktopHost` 普通用户令牌 Medium IL 后台预热、复接 `SharedRawMouseInputRouter` 在右键按下时预聚焦桌面覆盖层并授权前景；开启 `native_windows_desktop` 时自动休眠）。
  - `TouchpadThreeFingerSearchController.cs`：压力触控板三指轻触智慧搜索开关（`HKCU\SOFTWARE\PCManager\TouchPadSetting` + HID 报文）。

### B. 控制中心端（`ControlCenterSpeedBoost.dll`，.NET 4.8）
- `ControlCenterSpeedBoost/ControlCenterSpeedBoost.cs`：
  - `ControlCenterDomainManager` + `ControlCenterWindowOptimizer` 挂载 `AiDesktopMainWindow`。
  - 负责离屏预热、跳过隐藏清内存（`ReleaseMemory` / `MagicSpaceTurbo`）、软件渲染切换、3 分钟闲置温和释放工作集、`DWMWA_CLOAK = 13` 统一首帧同步，以及对 `TrustCircleView` 轮询做 60 秒节流消除二次闪烁。

### C. 通知中心端（`MessageCenterBridge.dll`，.NET 4.8）
- `MessageCenterBridge/MessageCenterBridge.cs`：
  - `MessageCenterDomainManager` 监听桥接 JSON，在 `MessageCenterUI` 中动态插入/更新/移除 `TipsSuperNotification` 卡片，并在宿主窗口与卡片两侧挂载 `handledEventsToo: true` 的鼠标/触控预览拦截回写 `ack`。
  - 注：通知中心中标题为「来自您的“XXX”」的是荣耀自带手机通知协同（点击打开多屏协同）；本工具箱桥接的是带对应应用图标与「点击跳转查看未读消息」的 PC 应用通知。
  - 拍平 `SlideInAnimation` 关键帧起始透明度（`1.0`）并配合 `DWMWA_CLOAK = 13` 首帧同步消除点击透底闪烁，同时支持软件渲染与 3 分钟闲置工作集释放。

---

## 3. 配置、IPC 与日志路径

- **用户配置**：`%APPDATA%\taskbarAndAIbar\adaptive-topbar.json`
- **工具箱诊断日志**（位于 `%TEMP%\MagicTaskbar\`）：
  - `adaptive-topbar.log`（顶部栏 / Dock / 设置 / 托盘转发）
  - `control-center-boost.log`（控制中心优化与内存释放）
  - `message-center-bridge.log`（通知中心卡片桥接与闪烁/唤醒优化）
- **通知桥接 IPC 文件**（位于 `%TEMP%\MagicTaskbar\`）：
  - `bridged-notifications.json`、`bridged-notifications-ack.json`、`bridged-icons\`
- **荣耀官方日志（对比排查时参考）**：
  - `C:\ProgramData\Comms\PCManager\log\usr\NotifyControlCenterUI.log`
  - `C:\ProgramData\Comms\PCManager\log\usr\MessageCenterUI.log`
  - `C:\ProgramData\Comms\MagicAnimation\log\usr\DesktopHost_*.log`
  - `C:\ProgramData\Comms\MagicAnimation\log\usr\MenuHostNative_*.log`

---

## 4. 构建、部署与注意事项

- **完整编译**（在仓库根目录执行，会同时产出 `.NET 6` 与 `.NET 4.8` DLL 到 `bin\Release\net6.0-windows\`）：
  ```powershell
  dotnet build TopBarAdaptiveColor.csproj -c Release
  ```
- **部署与还原**：
  - 安装 / 覆盖升级：双击 `一键安装.bat`（调用 `apply_adaptive_topbar.ps1`，会自动备份原始配置为 `.bak` 并重启相关进程）。
  - 卸载还原：设置页底部点击「卸载并还原」，或双击 `一键卸载.bat`（调用 `restore_topbar.ps1`）。
- **环境提示**：本仓库构建与荣耀客户端运行均依赖 Windows 环境；若从 WSL 会话打开项目，请使用 `/mnt/d/Desktop/magic/TopBarAdaptiveColor` 路径访问文件，实际编译 `.NET 6 + WPF` 与部署测试请在 Windows 终端或 Windows 工作区下进行。

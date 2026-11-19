# Magic 视界工具箱（HONOR Magic Vision TopBar & Dock Enhancements）

针对荣耀笔记本 **Magic 视界（HONOR MagicBook Art 14 / MagicTaskbar / ControlCenterTrayUI / ForcePad）** 的模块化运行时增强补丁包。

> **零侵入设计**：不修改任何官方已签名二进制文件（`magictaskbar-ui.exe`、`MagicTaskbar.*.dll`、`ControlCenterTrayUI.exe` 签名均保持原样），仅通过 .NET 6 `STARTUP_HOOKS` 与 .NET Framework 4.8 `AppDomainManager` 在内存中按需挂载。所有功能均可在 **Magic 视界设置 →「Magic 视界工具箱」** 中独立实时开关，并支持在设置页内或脚本一键彻底卸载还原。

---

## 快速安装与卸载（零命令行）

### 一键安装
1. 下载 [Releases](../../releases) 中的压缩包并解压（或克隆源码仓）。
2. 双击 **`一键安装.bat`**（或在源码目录中运行）。
3. 弹窗提示管理员权限（UAC）时点击 **“是”**，脚本会自动停止顶部栏/控制中心进程、部署模块并重启为正常用户权限。

> 若压缩包内已包含预编译的 `bin/Release/net6.0-windows/` 产物，安装**无需**本机安装 .NET SDK；若从纯源码克隆，脚本会自动调用 `dotnet build -c Release`。

### 一键卸载（三种方式任选）
- **设置界面内卸载**：打开 Magic 视界设置 → 左侧 **「Magic 视界工具箱」** → 最下方 **「卸载并还原」** 按钮 → 确认后授权 UAC，自动清理所有文件与配置并重启官方原版。
- **双击卸载**：双击目录中的 **`一键卸载.bat`**。
- **命令行还原**：管理员 PowerShell 运行 `restore_topbar.ps1`。

---

## 功能总览与实现原理

### 状态栏增强
1. **全屏/最大化时状态栏自适应颜色**
   通过 `EnumWindows` Z 序枚举与 `DWMWA_EXTENDED_FRAME_BOUNDS` 识别固定模式下真正覆盖工作区顶部的最大化窗口，结合 `BitBlt` 在接缝下方采样聚类提取标题栏主色，再按 ITU-R BT.709 相对亮度（带迟滞阈值）自动切换状态栏文字与 `SharpVectors` SVG 图标明暗（保留充电绿、红点等状态色）。
2. **显示/隐藏右侧 YOYO Claw 按钮**
   监听并同步接管状态栏右上角 `YoyoClawButton` 的 `Visibility` 属性，覆盖宿主异步状态刷新，实现实时显隐切换。
3. **顶部栏通知图标原地右键联动原生菜单（通用兼容）**
   拦截顶部状态栏固定图标（微信、飞书、QQ、钉钉、Everything、OneNote、PixPin / Qt5 / Qt6、Electron 等）的右键事件，枚举托盘窗口与 `HWND_MESSAGE` 隐藏窗口并按固定表 + **PE 导入表 / 延迟加载表（`DataDirectory[1/13]`）自动静态反汇编识别 `Shell_NotifyIconW/A` 与 `RegisterWindowMessageW/A` 回调消息**，直接在原程序隐藏托盘句柄原地 `PostMessage` 弹出原生右键菜单（按住 `Shift` 右键保留「从顶部栏移除」）。
4. **支持拖拽任意系统溢出托盘图标固定到顶部栏**
   展开 Windows `^` 隐藏托盘溢出窗后，通过全局鼠标钩子结合 UIA 遍历、进程窗口标题、注册表中英文别名以及裁剪前景像素的 16×16 视觉相似度匹配识别任意应用，拖拽到顶部栏即可持久化固定（支持重启恢复、左键激活、右键原生菜单及防“空壳”脏固定过滤）。
5. **顶栏空白处拖拽与双击控制最大化窗口**
   无任何额外可见标题栏控件：在固定模式下在顶栏空白区按下左键向任意方向拖动超过 `6 DIP`，立即向底层最大化窗口发送还原并跟手移动窗口；双击顶栏空白处直接切换还原/最大化。

### 底部任务栏（Dock）多任务增强
6. **中键/关闭单窗口后保持多窗口缩略图展开**
   拦截 `DockPreviewWindow` 关闭卡片后触发的 `PreviewInteractionCompleted -> HidePreview()`，转为就地移除目标项、重排并刷新布局（含 6 卡片切 5 卡片等版式切换重定位），让后续卡片留在光标下方方便连续关闭。
7. **悬停缩略图卡片时桌面实时预览（Aero Peek）**
   悬停 `DockPreviewWindow` 卡片时在桌面层下方显示点击穿透、不抢焦点的 `AeroPeekOverlayWindow`（含半透明深色遮罩与 `DwmRegisterThumbnail` 原窗实时镜像，兼容最小化窗口兜底遮罩），移开鼠标自动还原。
8. **底部 Dock 多窗口预览斜向防误关走廊**
   结合几何触发走廊判定与斜向轨迹/邻居驻留延迟切换，防止鼠标从 Dock 图标斜向移向缩略图卡片穿过分隔线或短暂擦碰相邻图标时误关或切走预览。

### 问题修复与性能体验优化
9. **状态栏靠右弹窗屏幕边界钳位**
   监听 `TopBarAcrylicPopupWindow` 打开与尺寸变化，自动将电池/音量/网络弹窗与亚克力背景钳位到当前显示器工作区安全范围内，避免隐藏部分按钮后弹窗右移出屏被裁切。
10. **修复最大化后状态栏下方细缝**
    监听顶部工作区保留协调引起的窗口位置偏移，在需要时将顶部状态栏底部向下微延展 `1-3 px` 盖住壁纸细缝，图标层锁定顶部不变。
11. **控制中心与设置点击响应极速优化**
    - 状态栏侧将按钮改为 `ClickMode.Press`（按下即响应）、直接向已驻留的 `HonorControlCenterWndTray` 发送异步消息、并让 `TopBarSettingsWindow` 关闭时转 `Hide()` 热复用。
    - 控制中心侧通过 .NET 4.8 `AppDomainManager`（`ControlCenterSpeedBoost.dll`）预热亚克力与可视化树、清除隐藏后冷却与内存工作集限流、延后重扫描回调，并同步隐藏高斯模糊背景层消除关闭闪现。
12. **顶部通知按钮极速响应与假死自愈**
    按下即直连向已运行的 `MessageCenterUI` 发送 `WM_SECONDOPEN`（`0x0800`）零进程冷启直接唤起，跳过冗余预检与重复打开；若通知进程无响应卡死则自动清理重启兜底，并在已展开状态点击避免重复闪烁。
13. **压力触控板三指轻触“智慧搜索”独立开关**
    同步读写 `HKCU\SOFTWARE\PCManager\TouchPadSetting`（`TripleFingerLightTouch` 与手势列表中的 `27`）并直接向 Goodix ForcePad 厂商 HID 接口下发特征/控制指令（`[0E 11 00/01]`），单独禁用三指轻触触发智慧搜索而不影响三指拖拽与其他手势。

---

## 目录说明
- `一键安装.bat` / `apply_adaptive_topbar.ps1`：自动请求管理员提权部署并重启相关 UI 进程。
- `一键卸载.bat` / `restore_topbar.ps1`：一键完整清理并还原官方原版状态（同时内嵌/复制进运行目录以供设置界面卸载按钮调用）。
- `StartupHook.cs` / `TopBarHookCoordinator.cs`：.NET 6 运行时启动钩子入口与挂载调度。
- `TopBarSettingsInjector.cs` / `ToolboxModuleRegistry.cs` / `AdaptiveSettingsStore.cs`：设置界面「Magic 视界工具箱」标签页注入、模块注册与配置持久化。
- `ControlCenterSpeedBoost/`：控制中心（.NET 4.8）响应加速模块。
- 其余各独立功能模块：`AdaptiveTopBarController.cs`、`NativeWindowSampler.cs`、`SvgIconThemer.cs`、`TopBarUiEnhancer.cs`、`StatusNotificationTrayRelay.cs`、`HiddenTrayPinEnhancer.cs`、`TopBarMaximizedWindowController.cs`、`TopBarAcrylicPopupClamper.cs`、`DockPreviewEnhancer.cs`、`TouchpadThreeFingerSearchController.cs`。

> 诊断日志位置：`%TEMP%\MagicTaskbar\adaptive-topbar.log` 与 `%TEMP%\MagicTaskbar\control-center-boost.log`。

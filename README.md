# Magic 视界工具箱 (Honor Magic Vision Toolbox)

针对荣耀笔记本 Magic 视界（顶部状态栏、底部 Dock、控制中心及压力触控板）的运行时扩展与问题修复工具。

- 项目仓库：https://github.com/spkyle/HonorMagicVisionToolbox
- 发行版本：https://github.com/spkyle/HonorMagicVisionToolbox/releases/latest
- 更新日志：[CHANGELOG.md](CHANGELOG.md)
- 开发与架构说明：[AGENTS.md](AGENTS.md)

本工具通过 .NET 6 `STARTUP_HOOKS` 与 .NET Framework 4.8 `AppDomainManager` 在进程启动时按需加载，不修改官方程序集文件及数字签名。所有功能均可在 **Magic 视界设置 →「Magic 视界工具箱」** 中独立开关或卸载。

---

## 安装与卸载

### 安装
1. 在 [Releases](https://github.com/spkyle/HonorMagicVisionToolbox/releases/latest) 下载压缩包并解压（或克隆本仓库）。
2. 双击运行 **`一键安装.bat`**。
3. 在 UAC 权限弹窗中选择 **“是”**，脚本将自动完成部署并重启顶部栏与控制中心进程。

> 发行包内已附带预编译程序集，无需安装 .NET SDK。若从源码目录运行安装脚本，将自动调用 `dotnet build -c Release`。

### 卸载
- **设置内卸载**：打开 Magic 视界设置 → **「Magic 视界工具箱」** → 底部点击 **「卸载并还原」**，授权 UAC 后自动清理全部文件并重启官方原版进程。
- **脚本卸载**：双击目录中的 **`一键卸载.bat`**（或在管理员 PowerShell 中运行 `restore_topbar.ps1`）。

---

## 功能说明

工具箱按 **桌面 / 顶部状态栏 / 底部 Dock 栏** 三大独立组件分组，支持自由组合取舍（每组主开关可单独关闭对应荣耀接管并还原 Windows 原生形态，各组件专属增强与修复自动归拢为其二级菜单并随主开关联动休眠）：

### 一、桌面
1. **关闭荣耀桌面接管（还原 Windows 原生桌面）**（默认保留荣耀桌面，本开关默认不勾选）  
   向荣耀桌面覆盖层（`DesktopHost.exe`）的官方隐藏消息窗发送 `WM_CLOSE` 优雅退出并恢复 Windows 原生 `SysListView32`，一步根治桌面右键延迟、左键框选低帧率与拖拽手感问题；随时取消勾选（或关闭总开关 / 卸载）将以普通用户权限（Medium IL）自动拉起 `DesktopHost.exe` 无损恢复荣耀桌面。
   - **↳ 二级菜单 · 桌面右键加速与防假死预热**（保留荣耀桌面时生效）：解除 `DesktopHost.exe` / `MenuHostNative.exe` / `CardMenuHostNative.exe` 的 Windows 11 效能模式（EcoQoS）限频与低内存优先级，在 `MenuHostNative.exe` 执行命令自回收退出后自动以普通用户权限后台预热，并复接顶栏共享 `SharedRawMouseInputRouter` 在右键按下时提前预聚焦桌面覆盖层。

### 二、顶部状态栏
2. **关闭顶部状态栏**（默认保留顶部状态栏，本开关默认不勾选）  
   优雅关闭荣耀顶部状态栏并释放顶部工作区，最大化窗口占满屏幕顶部；关闭期间会在系统托盘自动保留「Magic 视界工具箱」图标，点击可随时重新打开设置页或一键恢复顶栏。
   - **↳ 全屏与最大化时状态栏自适应配色**：识别当前最大化窗口，`BitBlt` 采样主色调并按 ITU-R BT.709 亮度自适应深浅文字与 SVG 图标，同时过滤 PixPin 等录屏浮层避免顶栏误隐藏。
   - **↳ 修正窗口最大化后顶栏下方缝隙**：工作区重算导致最大化窗口下移 `1–3 px` 产生桌面透缝时，向下微调状态栏背景容器底边封住缝隙。
   - **↳ 修正右侧弹出面板超出屏幕边缘**：隐藏部分右侧系统图标后，限制控制中心、电池、音量、网络等弹窗及亚克力背景层不超出当前显示器工作区。
   - **↳ 显示右侧 YOYO Claw 按钮**：控制状态栏右侧控制中心旁 YOYO Claw 按钮的显示状态。
   - **↳ 微信等应用未读消息收纳至通知中心与红标震动提醒**：将微信、飞书、钉钉、QQ 等顶栏未读消息同步生成卡片收纳到右上角通知中心，通知按钮高亮红色（`#ED3D37`）并约每 2 秒轻抖一次，点卡直接拉起原应用窗口（支持微信 4.x 最小化到托盘）。
   - **↳ 任意隐藏托盘图标拖拽固定与拖回**：展开 `^` 系统溢出托盘后可将任意程序图标拖到顶栏固定，或拖回 `^` 箭头收进隐藏托盘；已补齐 Win11 `DWMWA_CLOAKED` 校验避免钩子滞留。
   - **↳ 顶栏应用图标右键弹出原生托盘菜单**：静态扫描 PE 导入表/延迟加载表解析托盘回调消息，直接转发右键弹出软件原生菜单（按住 `Shift + 右键` 仍为“从顶部栏移除”）。
   - **↳ 顶栏空白处拖拽还原与双击控制**：在顶栏空白处按住左键拖动超过 `6 DIP` 即可还原并拖动当前最大化窗口，双击空白处切换最大化/还原。

### 三、底部 Dock 栏
3. **关闭底部 Dock 栏（还原 Windows 原生任务栏）**（默认保留底部 Dock 栏，本开关默认不勾选）  
   优雅关闭荣耀底部 Dock 栏、释放底部工作区并立即恢复 Windows 原生任务栏（多显示器及分辨率切换下自动守护）；随时关闭开关即可恢复荣耀底栏。
   - **↳ 底栏开始按钮右键弹出原生 Windows 菜单**：监听底栏 DirectComposition 宿主（`NativeCompositionDockWindow.ItemContextRequested`）对开始按钮（`utility:start`）的右键事件，触发原生 Windows 快捷菜单（`Win + X`，含任务管理器、终端、设备管理器、电源选项、运行等）。
   - **↳ 底栏原生高刷解锁（同步显示器 VSync）**：突破 WPF `CompositionTarget.Rendering` 60Hz 上限与鼠标移动 `15ms` 输入节流，在底栏动效活跃期按显示器硬件 `DwmFlush` 垂直同步直驱 DirectComposition 渲染帧，解锁 120Hz/165Hz 满帧波浪缩放与滑入滑出（闲置时 0% CPU 占用）。
   - **↳ 关闭单个窗口后保持多窗口缩略图展开**：中键或点击关闭单个窗口卡片时不收起其余预览窗口。
   - **↳ 悬停缩略图卡片实时桌面预览 (Aero Peek)**：鼠标悬停在缩略图卡片上时通过 `DwmRegisterThumbnail` 在桌面临时预览对应窗口完整画面。
   - **↳ Dock 缩略图斜向移动防误关**：从底部 Dock 图标斜向移至缩略图期间经过分隔线或擦过相邻图标时不中断预览。

### 四、通用性能与系统
4. **控制中心与设置快速响应与再点收起**  
   相关按钮改为按下即触发（`ClickMode.Press`），设置窗口隐藏热复用，控制中心关闭隐藏清内存、跳过防连点冷却并同步首帧显示（同时对 `TrustCircleView` 轮询节流消除二次闪烁），支持再次点击右上角控制中心图标收起面板。
5. **通知按钮快速响应、假死恢复与再点收起**  
   按下通知按钮直接通过 `WM_SECONDOPEN`（`0x0800`）唤醒常驻的 `MessageCenterUI`（响应异常时自动重建），关闭隐藏清内存、拍平起始透明度并同步亚克力层首帧显示以消除点击闪烁；再点图标即可收起面板。
   - **↳ 二级开关 · 秒开省内存 · 软件渲染降占**（默认开启）：控制中心与通知中心启用 WPF 软件渲染（`RenderMode.SoftwareOnly`），降低 GPU 渲染缓冲占用；可独立关闭恢复硬件加速。
   - **↳ 二级开关 · 秒开省内存 · 闲置自动释放工作集**（默认开启）：弹窗隐藏闲置 3 分钟后温和压缩工作集，配合软件渲染可将常驻内存从 `900–1000 MB` 缩减到 `30–60 MB`；可独立关闭保持热内存常驻最流畅。
6. **允许压力触控板三指轻触打开智慧搜索**  
   同步读写 `HKCU\SOFTWARE\PCManager\TouchPadSetting` 并向 Goodix ForcePad HID 下发控制报文，支持单独关闭三指轻触触发「智慧搜索」而不影响三指拖拽与其他多指手势。

---

## 日志位置
- `%TEMP%\MagicTaskbar\adaptive-topbar.log`
- `%TEMP%\MagicTaskbar\control-center-boost.log`
- `%TEMP%\MagicTaskbar\message-center-bridge.log`

---

## 安全说明与免责声明

1. **零官方二进制修改**  
   本项目源码全部公开，不修改、不替换、不破坏荣耀 Magic 视界、荣耀电脑管家及 Windows 系统的任何官方可执行文件（`.exe`）、动态库（`.dll`）及其数字签名。所有功能仅在运行期通过 .NET 官方扩展机制挂载：
   - 顶部栏与 Dock：`magictaskbar-ui.runtimeconfig.json` 配置 `STARTUP_HOOKS` 加载 `TopBarAdaptiveColor.dll`；
   - 控制中心与通知中心：通过 `ControlCenterTrayUI.exe.config` / `MessageCenterUI.exe.config` 配置 `.NET AppDomainManager` 加载 `ControlCenterSpeedBoost.dll` / `MessageCenterBridge.dll`。

2. **无后台服务与数据收集**  
   不安装系统服务、驱动程序或开机计划任务，不修改系统内核及系统保护文件，不连接第三方服务器，亦不收集、记录或上传任何用户聊天内容与个人数据。用户配置、图标缓存与运行日志均仅保存在本地当前用户目录（`%APPDATA%\taskbarAndAIbar\` 与 `%TEMP%\MagicTaskbar\`）。

3. **配置可备份与完整还原**  
   安装脚本在写入配置前会自动将原始 `.runtimeconfig.json` 与 `.exe.config` 备份为 `.bak` 文件；随时可在设置页「Magic 视界工具箱」底部点击 **「卸载并还原」**，或运行 **`一键卸载.bat`** / `restore_topbar.ps1`，自动恢复官方原始配置、触控板默认设置并清理全部补丁与临时文件。

4. **免责条款**  
   - 本项目仅作个人研究与桌面交互体验增强，与荣耀终端有限公司（HONOR）无任何官方关联或授权关系。  
   - 请通过本仓库官方页面获取源码或发行包，并在理解上述机制后自行决定是否使用。因官方软件版本更新、系统环境差异、第三方软件冲突或个人误操作造成的异常或损失，作者不承担相关责任；如官方更新后出现兼容问题，请先执行卸载还原。  
   - 下载、运行或二次分发本项目即视为您已知悉并同意本声明。

# Magic 视界工具箱 (Honor Magic Vision Toolbox)

针对荣耀笔记本 Magic 视界（顶部状态栏、底部 Dock、控制中心及压力触控板）的运行时扩展与问题修复工具。

- 项目仓库：https://github.com/spkyle/HonorMagicVisionToolbox
- 发行版本：https://github.com/spkyle/HonorMagicVisionToolbox/releases/latest
- 更新日志：[CHANGELOG.md](CHANGELOG.md)

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

### 一、状态栏
1. **全屏与最大化时状态栏自适应配色**  
   针对固定显示模式下最大化窗口顶部保留透明桌面条的问题，通过 `EnumWindows` 与 `DWMWA_EXTENDED_FRAME_BOUNDS` 识别当前最大化窗口，使用 `BitBlt` 在标题栏下方区域采样主色调并按 ITU-R BT.709 亮度自适应深浅文字与 SVG 图标（保留充电绿、未读红点等状态颜色）。同时过滤透明穿透窗体及 PixPin 等录屏区域框，避免全屏录屏时顶栏误隐藏或录屏结束后无法恢复。
2. **显示右侧 YOYO Claw 按钮**  
   接管宿主 `YoyoClawButton` 可见性状态，支持在设置中隐藏状态栏右侧控制中心旁的 YOYO Claw 按钮。
3. **未读消息收纳至通知中心与红标震动提醒**  
   当微信、飞书、钉钉、QQ 等顶栏应用出现未读消息时，将其同步生成卡片收纳到右上角通知中心，并把右上角通知按钮高亮为红色（`#ED3D37`）及约每 2 秒轻抖一次；点击通知中心中的卡片可直接跳转并激活原应用窗口，点 `×`、清空或在原应用内已读后自动消散并复位通知按钮。
4. **任意隐藏托盘图标拖拽固定与拖回**  
   展开 `^` 系统溢出托盘后，通过鼠标钩子结合 UIA 遍历、进程标题、注册表与 16×16 视觉相似度匹配识别任意应用，拖至顶栏即可固定；将顶栏固定图标或系统图标直接拖到 `^` 箭头上松手（或向下拖出状态栏 / `Shift + 右键`）可收进隐藏托盘。
5. **顶栏应用图标右键弹出原生托盘菜单**  
   拦截顶栏应用图标右键事件，结合内置规则与 PE 导入表 / 延迟加载表（`DataDirectory[1/13]`）静态扫描 `Shell_NotifyIconW/A` 与 `RegisterWindowMessageW/A` 解析回调消息，直接向原程序托盘句柄转发右键以弹出软件原生菜单（按住 `Shift + 右键` 仍为“从顶部栏移除”）。
6. **顶栏空白处拖拽还原与双击控制**  
   在顶栏空白处按住左键拖动超过 `6 DIP` 即可还原当前最大化窗口并跟随鼠标移动；双击空白处可直接切换最大化/还原状态。

### 二、Dock 栏
7. **关闭单个窗口后保持多窗口缩略图展开**  
   中键或点击关闭按钮关闭缩略图中的单个窗口时，仅移除已关闭卡片并刷新排列，不收起其余预览窗口。
8. **悬停缩略图卡片实时桌面预览 (Aero Peek)**  
   鼠标悬停在缩略图卡片上时，通过点击穿透覆层与 `DwmRegisterThumbnail` 在桌面临时预览对应窗口完整画面，移开鼠标后恢复原状态。
9. **Dock 缩略图斜向移动防误关**  
   鼠标从底部 Dock 图标斜向移至缩略图卡片期间，经过分隔线或擦碰相邻图标时不中断当前预览窗口。

### 三、问题修复与性能
10. **修正右侧弹出面板超出屏幕边缘**  
    隐藏部分右侧系统图标后，自动限制控制中心、电池、音量、网络等弹窗及其亚克力背景层的位置，防止超出当前显示器工作区。
11. **修正窗口最大化后顶栏下方缝隙**  
    工作区重算导致最大化窗口下移 `1–3 px` 产生桌面透缝时，向下微调状态栏背景容器底边封住缝隙。
12. **控制中心与设置快速响应与再点收起**  
    相关按钮切换为 `ClickMode.Press`（按下即触发）、设置窗口改为 `Hide()` 热复用，挂载 `.NET 4.8 AppDomainManager` 离屏预热控制中心与可视树、取消防连点冷却、关闭隐藏时的工作集强制清出（跳过 `ReleaseMemory` 与 `MagicSpaceTurbo` 限流），同时通过 `MAGICTASKBAR_DISABLE_WORKING_SET_TRIM=1` 阻止顶栏进程在后台定时清出工作集，避免长时间运行后再次卡顿；同步模糊背景与控件首帧显示并支持再次点击右上角控制中心图标收起面板。
13. **通知按钮快速响应、假死恢复与再点收起**  
    按下通知按钮时直接通过 `WM_SECONDOPEN`（`0x0800`）唤醒常驻的 `MessageCenterUI`（使用 `SendMessageTimeout` 探测真实响应状态后再决定是否重建），同时在通知中心侧跳过隐藏/渲染后的 `ReleaseMemory` 工作集清出、加速滑入/滑出动画并重置二次点击冷却，避免长时间挂起后重新打开通知卡顿；再次点击通知图标可发送失活消息收起通知中心。
    - **二级开关 · 秒开省内存 · 软件渲染降占**（默认开启）：可单独关闭或与下方闲置释放叠加。开启后控制中心与通知中心改用 WPF 软件渲染（`RenderMode.SoftwareOnly`），减少约 `130+ MB` GPU 渲染缓冲占用；关闭后可恢复 GPU 硬件加速。
    - **二级开关 · 秒开省内存 · 闲置自动释放工作集**（默认开启）：可单独关闭或与软件渲染叠加。开启后解除顶栏裁剪锁并允许回收托管垃圾，弹窗隐藏闲置 3 分钟后压缩工作集大幅降低常驻内存占用（久置后首次点开可能有短暂缺页微顿）；关闭后可保持热内存常驻最流畅。
14. **允许压力触控板三指轻触打开智慧搜索**  
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

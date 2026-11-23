# 更新日志 (Changelog)

所有重要变更均记录在本文件中。

## [v1.0.1] - 2026-11-23

### 新增与调整
- **默认开启「秒开省内存」两项二级优化**：  
  在「Magic 视界工具箱」设置与 `.NET 4.8 AppDomainManager` 配置解析中，将 **秒开省内存 · 软件渲染降占**（`fast_open_software_rendering`）与 **秒开省内存 · 闲置自动释放工作集**（`fast_open_idle_memory_trim`）默认调整为开启，同时保留两项二级开关可随时独立关闭或叠加组合。
- **同步文档**：新增 `CHANGELOG.md`，并更新 [README.md](README.md) 中的默认行为说明与更新日志入口。

### 问题修复
- **修复通知中心与控制中心/快捷设置弹窗点击闪烁问题**：
  - **通知中心（`MessageCenterBridge`）**：将 `SlideInAnimation` 关键帧起始透明度由 `0.0` 拍平至目标透明度 `1.0` 并在重新打开时停止未完成的退出动画，同时对主窗与 `_compositionHandleHolder` 亚克力背景层统一使用 `DWMWA_CLOAK = 13` 同步到首帧 `DispatcherPriority.Render` 绘制完成后再同时解除遮罩，消除点击瞬间先透底再突现的闪烁。
  - **控制中心与顶栏快捷弹窗（`ControlCenterSpeedBoost` / `TopBarAcrylicPopupClamper`）**：
    - 消除 `_compHolderWindow` 在 `DispatcherPriority.Input` 与 `DispatcherPriority.Render` 之间的双次解除遮罩竞态，并在待执行的 `Loaded` 界面刷新完成后再于 `Render` 阶段统一显示首帧；同时对 `TrustCircleView` 的快速连续重复轮询增加 60 秒节流（保留启动预热及长间隔刷新），消除打开约 0.5–1 秒后底部推荐卡片反复拆建并触发亚克力层重排带来的二次闪烁。
    - 修正 `TopBarAcrylicPopupClamper` 上边界由 `workDip.Top + 2.0` 改为 `workDip.Top`，避免 Wi‑Fi / 蓝牙等顶栏快捷弹窗在打开及约 1.2 秒后列表刷新重置位置时被反复向下推移 `2.5px` 并重绘背景。
- **修复通知中心桥接卡片（如 PC 微信通知）点击无响应并支持唤醒托盘最小化应用**：
  - 修复 WPF `FrameworkElement.LoadedEvent` 类处理器在 `TipsSuperNotification` 上未触发导致卡片从未挂载点击监听的问题，改为在宿主窗口与卡片两侧均挂载 `handledEventsToo: true` 的鼠标与触控预览拦截（`PreviewMouseLeftButtonDown/Up` 与 `PreviewTouchDown/Up`），并在卡片添加与窗口显隐后同步补挂与图标修复。
  - 修复最小化到系统托盘的微信 4.x（`Weixin.exe` 等 Qt / `MainWindowHandle == 0` 进程）在点击通知卡片后无法拉起窗口的问题：当目标窗口不可见时通过 `ack` 协同 TopBar `StatusNotificationTrayRelay` 按原托盘协议激活（含双击升级与启动回退），避免仅消除红标但不弹窗。
  - 说明：通知中心中标题为「微信 · 来自您的“XXX”」的卡片为荣耀电脑管家/智慧互联自带的手机通知协同（点击会打开荣耀多屏协同），带绿色微信图标、副标题为「点击跳转查看未读消息」的卡片为本工具箱桥接的 PC 微信通知。

### 性能优化
- **降低浅色最大化窗口下顶栏背景的后台轮询开销**：`AdaptiveTopBarController` 对未发生深浅切换的浅色背景视觉树遍历重同步增加 `1500ms` 节流（列表项容器新增或变化时仍通过 `ItemContainerGenerator.StatusChanged` 立即同步）。
- **消除窗口取色每帧托管内存分配**：`NativeWindowSampler.SampleStripDominantColor` 复用 `[ThreadStatic]` 像素字节缓冲、量化颜色桶字典与行索引数组。
- **优化隐藏托盘固定项实时状态构建**：`NotifyIconCatalog.BuildLiveSnapshot` 改为仅按固定项目标进程名查询并跳过无关系统进程与主窗口标题枚举，同时优先按 `RegistrySubkeyName` 直查 `NotifyIconSettings` 注册表子键，仅在缺失或未命中时回退全表扫描。
- **减少通知桥接轮询磁盘 I/O 与 JSON 反序列化**：`StatusNotificationBridgeCoordinator` 在无活跃或未读状态且未收到 IPC 唤醒时跳过读取 `bridged-notifications-ack.json`，并按文件修改时间与长度缓存已解析结果，同时复用未变化的通知记录对象与图标解析结果。

---

## [v1.0.0] - 2026-11-23

### 首个正式发布版本
- 状态栏全屏与最大化窗口自适应配色、浅色背景对比度自适应及录屏浮窗过滤，最大化时状态栏微调与贴合。
- 右侧 YOYO Claw 按钮显示开关、隐藏托盘应用拖拽固定与拖回、顶栏应用图标右键原生托盘菜单、顶栏空白处拖拽还原与双击控制。
- 未读消息（微信、飞书、钉钉、QQ 等）收纳至通知中心与右上角红标轻抖提醒。
- Dock 栏关闭单窗保持多窗口缩略图展开、悬停桌面实时预览（Aero Peek）、斜向移动防误关。
- 右侧弹窗屏幕边缘防溢出修正、控制中心与通知中心按键即触发、设置页与弹窗热复用、闲置工作集降占保护与可选秒开省内存二级开关。
- 压力触控板三指轻触打开智慧搜索独立开关、设置内一键卸载与完整配置还原脚本。

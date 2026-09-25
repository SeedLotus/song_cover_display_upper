# 歌曲封面显示唱片机上位机开发日志

> 本文件记录 `song_cover_display_upper` 的开发修复、待修复问题、未来需求以及调试过程中的关键观察。  
> 维护者：Claude Code / WorkBuddy  
> 最后更新：2026-09-25

> **固件侧进展（2026-09-24 凌晨）**：固件仓库已 fork 至 SeedLotus/song_cover_display 并注册为兄弟子项目 `projects/song-cover-display`。
> 固件修复 `7328afd`（3.4/3.5 根因 + `/h` 心跳 + 越界写修复）与 GCC 构建体系 `6f70fb2`（Makefile + 224K 链接脚本）已提交推送，
> 本机已用 MSYS2 arm-none-eabi-gcc 13.3 编出 `build/firmware.hex`。**尚未烧录**——用户无调试器，
> 待确认 PCB 是否有 BOOT0 焊盘（有则 USB DFU，无则购 DAPLink 后用 pyocd 烧录）。
> 烧录后需验证：重连首封面（3.5）、60 秒不休眠（3.6）、无 AwaitingAck 超时提示（3.4）。

---

## 一、项目背景

- **上位机**：WPF (.NET 8) 桌面应用，使用 Windows SMTC 监听媒体会话/封面，通过 USB 虚拟串口（CDC）与下位机通信。
- **下位机**：AT32F403A + GC9A01 圆形屏，固件仓库为 `https://github.com/TiX233/song_cover_display.git`。
- **通信协议**：
  - 命令以 `/` 开头，以 `\n` 结尾：`/0` 暂停、`/1` 播放、`/t` 准备接收图片、`/o` 图片发送完毕、`/k` 准备好、`/a` 接收完毕、`/r{n}` 重传单个包、`/x{n}` 从某包重传。
  - 图片包以 `#` 开头，后跟 2 字节大端序号 + 61 字节 RGB565 数据，总包数固定为 `59 * 32 = 1888`。

---

## 二、已修复内容

### 2.1 串口帧解析与连接状态

- **问题**：原代码使用 `Encoding.ASCII.GetString` + `Contains/StartsWith` 解析串口数据，存在断包、粘包、`Contains` 误匹配、非 ASCII 字节被替换等问题。
- **修复**：
  - 在 `SerialPortService.cs` 中引入 `_receiveBuffer`，按 `\n` 分帧。
  - 新增 `SerialCommandReceived` 事件，提供结构化的 `CommandText`、`CommandType`、`PacketIndex`。
  - 修复 `UpdateConnectionState` 抑制重复事件，断开时清空 `_connectedPort`。
  - `IsConnected` 改为 `_serialPort?.IsOpen == true && _isConnected`。
  - 发送/接收异常时自动调用 `Disconnect()`。

### 2.2 USB 设备插拔检测与自动重连

- **问题**：USB 拔出/插入后上位机不会变红/重连。
- **修复**：
  - 引入 `System.Management.ManagementEventWatcher`（WMI）监听 `Win32_PnPEntity` 的 `__InstanceCreationEvent` 和 `__InstanceDeletionEvent`。
  - 修复 WQL `LIKE` 语句中反斜杠转义问题，使用 `LIKE 'USB%VID_2E3C%PID_2568%'`。

### 2.3 图片传输状态机

- **问题**：封面变化立即发 `/t`、收到 `/k` 立即发全图、发完直接发 `/o` 未等待 `/a`；快速切换封面会并发多次传输，导致下位机显示屏掉初始化或动画被打断。
- **修复**：
  - 在 `MainWindow.xaml.cs` 中引入 `ImageTransferPhase` 枚举：`Idle`、`Starting`、`Transferring`、`AwaitingAck`、`Completed`。
  - 串行化图片传输，取消旧传输，等待 `/a`，抑制 `StatusSendTimer` 在传输期间发送 `/1`/`/0`。
  - 实现 `/r` 和 `/x` 重传请求。

### 2.4 图片分包数与固件匹配

- **问题**：下位机固件 `PIC_PACK_NUMS = 59 * 32 = 1888`，上位机原代码发送 1889 包，导致下位机数组越界/封面显示异常。
- **修复**：`ImageProcessor.CalculatePacketCount` 与 `SerialPortService.CalculatePacketCount` 均限制最大包数为 1888。

### 2.5 UI 线程图片处理

- **问题**：`ProcessAlbumArtAsync` 在非 UI 线程访问 `BitmapSource` 等 DispatcherObject，抛出“调用线程无法访问此对象”。
- **修复**：将 `ProcessAlbumArtAsync` 调用移到 `Dispatcher.InvokeAsync` 中执行。

### 2.6 `/k` 等待死锁

- **问题**：下位机 `/k` 响应可能丢失，导致上位机一直等待。
- **修复**：增加 `WaitForKWithRetryAsync`，超时后回退到直接发送图片数据。

### 2.7 PC 播放/暂停立即同步摇臂

- **问题**：PC 端播放/暂停事件只更新 UI，不立即通知下位机，摇臂最长延迟 1 秒。
- **修复**：`OnPlaybackStateChanged` 中立即调用 `SendPlaybackStatus(isPlaying)`。

### 2.8 串口对象重连残留

- **问题**：`SerialPort` 实例复用，USB 断开后重新 `Open()` 可能出现底层句柄残留，表现为“已连接”但实际通信失败。
- **修复**：`Connect()` 前先 `Dispose` 旧 `SerialPort` 并重新创建新实例，启用 `DtrEnable`/`RtsEnable` 和读写超时。

### 2.9 发送数据 pacing

- **问题**：图片包发送过快可能冲垮下位机双缓冲。
- **修复**：`SendString`/`SendBytes`/`SendImagePacket` 每次 `Write` 后调用 `BaseStream.Flush()`，确保数据及时下发。

### 2.10 重连后封面同步增强

- **问题**：重连后封面无法同步到下位机。
- **修复**（仍在验证中）：
  - 重连稳定期从 2 秒延长到 4 秒。
  - `/0` 唤醒后等待 1 秒再发 `/t`，减少下位机状态机干扰。
  - 重连场景 `/t` 重试次数增加到 8 次。
  - `/a` 超时后自动重试发送一次 `/o`。
  - `HandleImageTransferAck` 收到 `/a` 后立即补发当前播放状态 `/1`/`/0`。

### 2.12 媒体身份去重（播放/暂停不再重复切封面）

- **问题**：Windows SMTC 在播放/暂停状态变化时也会触发 `MediaPropertiesChanged`，导致同一视频被反复处理、编码、发送。
- **修复**：在 `MainWindow.OnMediaInfoChanged` 中增加标题/艺术家/专辑身份判断；只有媒体身份真正变化时才进入 `ProcessAlbumArtAsync`，保留 hash 检查作为兜底。

### 2.13 图片处理与传输延迟优化

- **问题**：图片缩放/编码在 UI 线程阻塞，且每包 `Write + Flush` 开销大。
- **修复**：
  - `ProcessAlbumArt` 使用 `DispatcherPriority.Background` 调度。
  - RGB565 编码移到 `Task.Run` 后台线程。
  - `SerialPortService.SendImagePacket` 增加 `flush` 参数；新增 `SendImagePacketRange` 批量发送，默认每 8 包 Flush 一次。
  - `SendImageAsync` 改为批量调用，减少底层流刷新次数。

### 2.14 重连后封面同步握手增强

- **问题**：USB 重连后封面仍无法同步，除下位机固件 `pack_get_lost_counter` 未初始化外，上位机 `/t` 发送时机偏早、CDC 初始化尚未完全稳定也是诱因。
- **修复**：
  - 重连稳定期从 4 秒延长到 6 秒，唤醒 `/0` 后等待 1.5 秒。
  - 增加 `SerialPortService.ClearReceiveBuffer()`，在 `/0` 前后各清理一次接收缓冲区。
  - 增加 `WaitForLineQuietAsync` 线路安静检测，避免在下位机仍有输出时发送 `/t`。
  - 重连场景 `/t` 重试次数提升到 12 次，间隔 250ms。
  - `SerialPortService.OnStatusMessage` 增加 `[Serial]` 前缀的 `Debug.WriteLine` 输出，便于在 VS 输出窗口/DebugView 追踪时序。

### 2.15 开机自启动静默启动

- **问题**：开启开机自启动后，系统启动时主窗口会弹出。
- **修复**：
  - `App.xaml` 移除 `StartupUri`，设置 `ShutdownMode="OnExplicitShutdown"`。
  - `App.xaml.cs` 解析 `--silent` 参数，静默启动时只创建 `MainWindow` 不调用 `Show()`。
  - `AutoStartManager.CreateShortcut` 为快捷方式附加 `--silent` 参数。
  - 托盘菜单“退出”改为调用 `System.Windows.Application.Current.Shutdown()`。

### 2.16 快速切换视频封面错位修复

- **问题**：多个视频来回快速切换时，封面与视频错位（如 ABC 循环切换显示 BCA），甚至切换后完全不更新。
- **根因**：
  1. `ProcessAlbumArtAsync` 是异步的，`_currentImageRgb565Data` 作为共享字段在多个并发处理任务之间被覆盖，导致先完成的旧任务调用 `StartImageTransfer` 时实际上发送的是后一张封面。
  2. 播放器切换视频时可能先触发一个**无缩略图**的临时事件，代码在此事件中更新了 `_lastMediaTitle`，导致后续真正带缩略图的事件因 `identityChanged=false` 被跳过。
- **修复**：
  - `ProcessAlbumArtAsync` 增加 `title`/`artist`/`album` 参数，编码结果保存到局部变量。
  - 完成编码后校验当前媒体身份是否仍与处理时一致；若已切换，则丢弃本次结果，避免发送过时封面。
  - 仅身份未变时才更新 `_currentImageRgb565Data` 并启动 `StartImageTransfer`。
  - 无缩略图事件不再更新 `_lastMediaTitle`，避免跳过后续真正的封面处理。
  - `StartImageTransfer` 在 `Starting` 阶段直接替换为新图，避免 pending 丢失。
  - 收到 `/a` 后启动 pending 新图前增加 200ms 延迟。

### 2.17 重连后摇臂位置同步

- **问题**：重连后下位机被 `/0` 唤醒进入暂停态，若 PC 正在播放，摇臂会长时间放下直到封面同步完成。
- **修复**：在重连握手序列中，`/0` 唤醒并稳定后，立即补发一次当前播放状态 `/1`/`/0`，让摇臂在封面同步前到位。

### 2.18 摇臂状态同步模型重设计

- **问题**：手动拨动摇臂后，摇臂与 PC 播放状态错乱，甚至完全无法控制。
- **根因**：下位机固件把 `/1` 和 `/0` 都当作"切换状态"的 topic，而不是"设置播放/暂停"；上位机重复发送 topic 会导致下位机反向切换。
- **修复**：
  - 上位机引入 `_lowerMachinePlaying` 跟踪下位机 believed state。
  - 收到 `/q1`/`/q0` 时，更新下位机 believed state 并控制 PC 播放/暂停，等待 SMTC 事件后再发送 topic。
  - `OnPlaybackStateChanged`、`StatusSendTimer_Tick`、图片接收完成后，统一走 `SyncPlaybackStatusToLowerMachine()`，只在 PC 状态与下位机状态不一致时才发送 topic。

### 2.19 取消 `/o` 重试避免封面重复落下

- **问题**：A 切换 B 视频后，B 封面会连续播放两次从上往下的切换动画。
- **根因**：`/a` 超时后上位机重试 `/o`，下位机每次收到 `/o` 都会播放一次下落动画；若第一次 `/o` 未丢失，下位机就会播放两次。
- **修复**：取消 `/o` 重试；`/a` 超时后依赖后续切换视频重新传输，或重连后的 autoRetry 机制。

### 2.20 图片包发送事件 UI 刷新节流（2026-09-23）

- **问题**：`OnImagePacketSent` 每发一个包都 `Dispatcher.Invoke` 刷新状态栏，一张封面 1888 包 = 1888 次 UI 线程调度，与 2.13 的批量发送优化自相矛盾。
- **修复**：发送失败立即上报；成功进度每 128 包或最后一包才刷新一次；`Invoke` 改为 `InvokeAsync`。

### 2.21 批量发包锁粒度上提（2026-09-23）

- **问题**：`SendImagePacketRange` 逐包调用 `SendImagePacket`，每包独立 `lock/unlock`，批与批之间其他命令（心跳、`/q` 响应等）可插入发送，理论上会打乱包序。
- **修复**：提取 `SendImagePacketCore`（假定已持锁），`SendImagePacketRange` 在单次 `_serialLock` 内发完整批；同时把 `SendImagePacket` 的异常断开策略与 `SendString`/`SendBytes` 对齐（任何异常都断开重连，原来只对 IOException/InvalidOperationException 断开）。

### 2.22 移除封面处理死代码身份复核（2026-09-23）

- **问题**：`ProcessAlbumArtAsync` 编码完成后的身份复核（`title != _lastMediaTitle` 等）是死代码——`_processAlbumArtLock` 串行化整个流程，且 `_lastMediaTitle` 等字段仅由该函数在持锁期间更新，复核条件恒不成立。
- **修复**：移除死代码并注明真实保护机制（semaphore 串行化 + 缩略图 hash + `StartImageTransfer` pending 替换）。

### 2.23 进程退出统一清理资源（2026-09-23）

- **问题**：`App.OnExit` 只关闭 Mutex，`TrayService`/`SerialPortService`/`MediaService`/`IpcService` 均未 Dispose，进程退出后托盘图标残留（鼠标划过才消失），串口/媒体会话句柄悬挂。
- **修复**：`MainWindow` 新增 `CleanupServices()` 统一停定时器、取消传输、Dispose 三个服务；`App` 持有 `IpcService` 引用并在 `OnExit` 中一并清理。

### 2.24 图像处理与杂项清理（2026-09-23）

- **`ImageProcessor.CreateBlurredBackground`**：原实现创建 `BlurEffect` 和临时 `Image` 元素但从未实际渲染（死代码），所谓"模糊"实为缩小到约 20px 再放大的像素化柔焦。已移除死代码，视觉行为不变；删除无用的 `BLUR_RADIUS` 常量。
- **占位图**：原来分两次在同一坐标绘制 "T X" 和 "i" 互相重叠，合并为单次绘制 "TiX"。
- **串口命令分发**：`OnSerialCommandReceived` 由 `Dispatcher.Invoke` 改为 `InvokeAsync`，避免 UI 繁忙时同步阻塞串口接收线程。
- **`App.xaml.cs`**：移除被命名管道取代后的死常量 `WM_SHOW_APP`/`HWND_BROADCAST`；第二实例 IPC 信号发送失败时输出调试日志。
- **`MediaService.UpdatePlaybackInfoAsync`**：`GetPlaybackInfo` 为同步 API，移除多余的 async 修饰（CS1998）。
- **编译警告归零**：修复全部 nullable 标注问题（事件 sender 参数、`GetEmbeddedIconAsTempFile` 返回类型、`IpcService`/`AutoStartManager` 字段与局部变量）及未使用的 `ex` 变量。`dotnet build` 从 27 警告降至 0 警告 0 错误。

### 2.25 防抖 + 主动重取修复 SMTC 事件数据滞后（2026-09-23，实机复现后修复）

- **现象**：多次切换视频后，上位机窗口显示的是**上一个视频**的封面且不再更新，下位机随之卡住（README 第六节"新旧封面闪烁"已知问题的顽固形态）。
- **根因**：SMTC 的 `MediaPropertiesChanged` 触发瞬间，会话属性尚未更新——读到的是上一个媒体的标题/缩略图。旧代码直接使用事件时刻的数据，把新身份与旧封面绑定；随后真正的新封面事件因身份已被"更新"而被去重逻辑跳过，流水线永久卡死在旧封面。
- **修复**：
  - `MediaService` 新增 `GetCurrentMediaInfoAsync()`，主动拉取当前会话最新媒体属性。
  - `MainWindow.OnMediaInfoChanged` 改为：立即用事件数据刷新文本（保证响应），然后防抖 400ms（期间新事件会取消旧防抖），再调用 `GetCurrentMediaInfoAsync` 获取可信数据，修正文本并进入封面处理流水线。
  - 下游去重逻辑（身份判断 → hash 兜底 → semaphore 串行化）不变。
  - 增加 `[Media]` 前缀的 Debug 日志，记录防抖后的标题对比。

### 2.26 封面切换延迟优化（2026-09-23）

- **背景**：2.25 修复正确性后实机反馈切换延迟偏大。
- **优化项**：
  - 固定 400ms 防抖改为两段式拉取：120ms 首拉（尽快出图）+ 500ms 确认拉取（兜住 SMTC 滞后缩略图）。确认阶段通过 `forceHashCheck` 绕过身份捷径。
  - `ProcessAlbumArtAsync` 去重改为**纯 hash 判据**，移除"同身份直接跳过"捷径（它会挡住确认阶段的合法缩略图更新）；播放/暂停重复事件仍由上层身份捷径挡在解码之前，无额外开销。
  - 串口批量发送从 8 包/Flush 放宽到 16 包/Flush（236→118 次刷新）；丢包风险由 `/r`、`/x` 重传机制兜底，若实机出现掉初始化需回退此项。
  - 增加耗时日志：`[Media] 封面处理完成，耗时 Xms`、`[Transfer] /t→/a 耗时 Xms`，供实机定位瓶颈。

### 2.27 修复传输取消后状态机死锁（2026-09-23，实机复现）

- **现象**：PotPlayer 与 B站视频互相切换（跨 SMTC 会话）后，下位机锁死在 PotPlayer 封面，后续封面全部无法切换。
- **根因**：2.16 引入的 pending 机制存在收尾缺陷。新封面到达时若当前传输处于 `Transferring`/`AwaitingAck`，`StartImageTransfer` 会排队 pending 并取消当前传输，但被取消的一方均不复位状态：
  - `SendImageAsync` 捕获 `OperationCanceledException` 后直接"忽略"，`_imagePhase` 永久卡在 `Transferring`；
  - `WaitForPhaseTimeoutAsync` 的 `Task.Delay` 被取消后直接 return，超时保护失效，可能永久卡在 `AwaitingAck`。
  - 此后所有 `StartImageTransfer` 都走 pending 分支无限排队，状态机死锁。
- **修复**：
  - `SendImageAsync` 取消时：若仍是当前传输且处于 `Transferring`，复位为 `Idle` 并延迟 300ms 启动 pending 新图（下位机收到下一次 `/t` 会自行丢弃残包）。
  - `WaitForPhaseTimeoutAsync` 取消后不再直接返回，继续走阶段检查与超时收尾；收到 `/a` 的正常路径因阶段已变为 `Completed` 不受影响。
  - 防御性：`OpenReadAsync` 增加 3 秒超时，防止跨会话后旧会话缩略图流读挂导致 `_processAlbumArtLock` 被永久占用。

---

## 三、待修复问题

### 3.1 重连后封面仍无法同步（P0）—— 观察中

- **当前状态**：上位机侧已完成延迟、缓冲区清理、线路安静检测、诊断日志、批量发送等兼容性增强；若仍无法同步，核心根因将指向下位机固件 `pack_get_lost_counter` 未初始化问题，需配合下位机固件仓库修复。
- **建议下一步**：
  - 使用本轮新增的 `[Serial]` 调试日志，确认 `/t` 重试次数、`/k` 是否到达、`/a` 与 `/r`/`/x` 的时序。
  - 推动下位机固件修复 `pack_get_lost_counter` 中 `uint8_t i` 的初始化。

### 3.2 播放/暂停时封面重复切换（P1）—— 已修复

- **修复方式**：在 `MainWindow.OnMediaInfoChanged` 中增加标题/艺术家/专辑身份判断；同一媒体播放/暂停状态变化但身份未变时，跳过 `ProcessAlbumArtAsync`。
- **验证方向**：播放视频后暂停/播放，观察 `ImageTransferStatusText` 不应再出现“准备传输图片”等封面处理文本。

### 3.3 切换视频封面延迟仍可优化（P2）—— 已部分优化

- **当前状态**：图片处理已使用 `DispatcherPriority.Background` 调度，RGB565 编码已移到后台 `Task.Run`，串口发送已改为批量 Flush（每 8 包刷新一次）。
- **可继续微调**：根据实机测试调整 `BATCH_SIZE` 与 `flushEvery`，在延迟与 USB CDC 缓冲安全之间取得平衡。

### 3.4 正常切换视频偶发显示"图片传输超时 AwaitingAck"

- **现象**：封面实际能正确显示在下位机，但上位机 `ImageTransferStatusText` 偶发显示 `"图片传输超时: AwaitingAck"`。
- **根因分析**：
  - 下位机收到 `/o` 后，依赖 `g_tx_completed` 标志发送 `/a`；若该标志为 false，`/a` 会被静默丢弃。
  - 上位机取消 `/o` 重试后，若 `/a` 在 1000ms 超时窗口内未到达，上位机会认为传输失败，但下位机实际上已正确显示图片。
- **已尝试修复**：
  - 延长 `/a` 超时到 2000ms：测试中出现封面不同步，已回退。
  - 恢复 `/o` 重试：会导致同一封面落下两次动画，未采用。
- **当前策略**：保持 1000ms 超时、不重试 `/o`，接受偶发的超时提示；该提示不影响实际功能。
- **彻底修复方向**：
  - 方案 A（推荐）：修改下位机固件，确保 `/a` 可靠回包，或增加 ACK 重传机制。
  - 方案 B：上位机增加"查询/心跳"协议，在 `/a` 超时后主动查询下位机接收状态。

### 3.5 USB 重连后第一个当前播放视频封面未同步

- **现象**：USB 拔插重连后，下位机保持默认 TiX 字样，不显示当前正在播放的视频封面；切换到第二个视频后恢复正常。
- **根因分析**：
  - 下位机固件 `pack_get_lost_counter()` 中的 `uint8_t i;` 未初始化，导致首次图片传输时误报丢包，下位机不发 `/a`、不显示图片。
  - 上位机已增加重连后 6 秒稳定期、`/0` 唤醒、接收缓冲区清理、12 次 `/t` 重试、首次 `/a` 超时后自动完整重试一次，仍无法完全规避该固件缺陷。
- **已尝试修复**：
  - 重连后延迟刷新媒体会话：未解决，已回退。
  - 增加首次 `/a` 超时后的多次完整重试：引入时序问题，已回退。
- **当前策略**：保持一次自动完整重试；若仍失败，依赖用户切换第二个视频恢复。
- **彻底修复方向**：必须修改下位机固件，初始化 `pack_get_lost_counter` 中的循环变量。

### 3.6 保活心跳与下位机 60 秒休眠的协议矛盾（2026-09-23 新识别，同日修复）

- **现象推测**：README 协议要求"上位机需定期发送 `/1` 或 `/0` 避免下位机 60 秒休眠"，但 2.18 重构后 `SyncPlaybackStatusToLowerMachine()` 只在 PC 状态与下位机 believed state **不一致**时才发 topic，状态一致时 `StatusSendTimer` 每秒空转、实际什么都不发 → 正常听歌约 60 秒后下位机可能休眠。
- **矛盾本质**：下位机把 `/1`、`/0` 当作"切换状态"的 topic 而非幂等的"设置状态"，周期心跳会翻转摇臂；不发又会休眠。上位机单侧无法两全。
- **疑似关联**：README 第六节"上位机挂后台太久时可能不会响应下位机的手动拨动唱臂命令"可能并非上位机失活，而是下位机已休眠。
- **修复（2026-09-23，上下位机配套）**：
  - 固件新增 `/h` 心跳命令：仅调用 `_usb_get_up()` 重置休眠闹钟，不发布 topic、不碰显示。
  - 上位机 `StatusSendTimer_Tick`：状态不一致时仍发 `/1`/`/0` topic；一致时改发 `/h`。
  - 兼容性：新上位机 + 旧固件 → `/h` 被忽略，行为同旧版（60s 休眠）；旧上位机 + 新固件 → 行为同旧版。两侧独立升级安全。
  - 另：固件 `#` 图片包接收也计入活动（重置休眠计时），避免长传输期间倒计时归零。
- **待验证**：实机测试——连接后正常播放 60 秒以上不做任何操作，屏幕应保持常亮、拨唱臂应正常响应。

### 3.7 网易云音乐：切换/暂停歌曲导致封面多次刷新且封面不再转动（v1.1.0 用户反馈）—— 停转已修复（待实机验证），多次刷新待日志

- **现象（2026-09-24 用户实机反馈，v1.1.0 包）**：使用网易云音乐播放时，切换歌曲和暂停歌曲会导致封面**多次刷新**；并且封面**不再转动**（已确认指唱片机屏幕图片旋转停止，网易云特有，PotPlayer 对照正常）。
- **停转根因（2026-09-25 双仓库代码走读确认，非推测）**：固件 `/t` 停转、仅 `/1` 恢复；上位机 believed-state 模型在网易云"全程 Playing"场景下永远不发恢复 `/1`。修复见 2.31（协议 v2 幂等设态 + 无条件补发 + 版本协商，v1 固件零回归）。
- **多次刷新**：待 v1.2.0 实机日志（2.30）确认事件序列后处理，初步方向为网易云渐进式缩略图更新。
- **验证清单（烧录 v2 固件 + v1.2.0 上位机后）**：网易云连续切歌 ≥5 次，每次封面到位后转动应恢复；暂停/播放摇臂响应正常；`logs/` 中不应出现 topic 发送后 believed 与现象不符。

---

## 四、未来新需求

- 暂无新的未来需求。
- ~~开机自启动静默启动~~ 已实现（启动参数 `--silent`）；2026-09-24 升级为可选设置「静默启动」（见 2.28）。

### 2.28 可选设置「静默启动」+ 托盘双击呼出（2026-09-24）

- **需求**：主界面新增「静默启动」复选框；勾选后无论开机自启还是手动启动，均不弹出主窗口、仅显示托盘图标；双击托盘图标可呼出窗口。
- **实现**：
  - 新增 `upper/Services/AppSettings.cs`：JSON 持久化到 exe 同目录 `settings.json`（便携模式），读取失败静默回退默认值。
  - `App.xaml.cs`：静默判定改为「命令行 `--silent`（自启快捷方式携带）**或** 设置勾选」。自启快捷方式仍固定携带 `--silent`，因此不勾选时行为与旧版一致（自启静默、手动弹窗）。
  - `MainWindow.xaml(.cs)`：新增「静默启动」复选框，勾选变更即时保存，下次启动生效。
  - `TrayService.cs`：托盘呼出窗口由左键**单击**改为**双击**（避免单击误触，且避免单击+双击重复触发）；托盘右键菜单「显示主窗口」不变。
- **兼容说明**：静默启动状态下手动再次启动（第二实例）仍会通过 IPC 通知主实例弹窗，符合用户预期。

### 2.29 托盘右键菜单新增「开机自启动」「静默启动」可勾选项（2026-09-24）

- **需求**：托盘图标右键菜单中加入两个开关项，并实时显示当前勾选状态。
- **实现**：
  - `TrayService.cs`：新增 `AutoStartToggleRequested` / `SilentStartToggleRequested` 事件与 `SetAutoStartChecked` / `SetSilentStartChecked` 状态同步方法；菜单项 `CheckOnClick = true`。
  - `MainWindow.xaml.cs`：托盘菜单点击 → 翻转主窗口对应复选框，复用既有 Checked/Changed 处理逻辑（权威状态在复选框/`AutoStartManager`/`AppSettings` 一侧）；两个处理器末尾回同步托盘勾选状态，操作失败回滚后也能保持一致。
  - 初始状态：构造函数中 `InitializeAutoStart` 与静默启动复选框初始化时触发处理器，自动完成托盘勾选状态初始化。

### 2.30 文件诊断日志（2026-09-25）

- **背景**：Release 包无法抓 Debug 输出，3.7 网易云问题需要实机事件序列。
- **实现**：新增 `upper/Services/FileLogger.cs`——exe 同目录 `logs/upper-yyyyMMdd.log`，保留 7 天，写文件同时镜像 Debug。
- **覆盖点**：SMTC 事件（MediaPropertiesChanged/PlaybackInfoChanged/会话切换，含非当前会话丢弃）、媒体流水线决策（拉取数据/hash 跳过/封面处理耗时）、传输状态机（启动/pending/取消/超时/t→a 耗时）、播放同步（topic 发送与 believed 变迁、瞬态跳过）、串口消息（除 /h 心跳外全量）。

### 2.31 网易云转盘停转修复：瞬态状态治理 + 协议 v2 版本协商（2026-09-25）

- **根因（双仓库代码走读确认）**：固件收到 `/t` 会 `disp_pic_rotate(0)` 停转，**全固件只有 `/1` 能恢复转动**。上位机 `SyncPlaybackStatusToLowerMachine()` 只在 believed 不一致时发 `/1`；网易云切歌全程保持 Playing，believed 无变化 → 恢复 `/1` 永远不发 → 转盘永久停转。旧代码里网易云恢复转动实际依赖 Changing 瞬态触发的"暂停-播放" toggle 对，传输风暴中丢一个包即失同步死转。
- **修复（上下位机配套，协议 v2）**：
  - 固件 `/1` `/0` 改为幂等设态：仅状态翻转时发布摇臂 topic（消除 toggle 丢包失同步整类问题）；`disp_pic_rotate` 显示侧无条件执行（幂等，承担转动恢复）。新增 `/v` 查询回复 `/v2`。
  - 上位机连接后发 `/v` 协商；收到 `/v2` 置 `_firmwareSupportsIdempotentPlayback`。
  - v2 下：`/a` 收到后延迟 1000ms（避开下落动画）**无条件补发**当前播放状态，恢复转动；Changing/Opened 瞬态不再驱动同步。
  - v1 固件（未烧录设备）：全部保持 legacy 行为，零回归。
- **多次刷新症状**：根因待实机日志确认（2.30 日志已就位），初步方向为网易云渐进式缩略图更新触发多次完整传输。

---

## 五、调试过程关键反馈

| 时间 | 测试项 | 结果 | 关键信息 |
|------|--------|------|----------|
| 2026-07-14 | 初次拉取与编译 | 成功 | 项目为 .NET 8 WPF，依赖 `System.IO.Ports`、`System.Management` |
| 2026-07-14 | 帧解析器 + 连接状态 | 通过 | 快速拨动摇臂不再漏命令 |
| 2026-07-14 | USB 插拔变红/重连变绿 | 通过 | WMI 事件有效 |
| 2026-07-14 | 切视频封面 | 初步通过 | 但存在延迟与偶发掉初始化 |
| 2026-07-14 | 分包数匹配 | 通过 | 下位机不再因 1889 包越界 |
| 2026-07-15 | PC 播放/暂停控制摇臂 | 通过 | 立即下发 `/1`/`/0` 后响应较快 |
| 2026-07-15 | 重连后封面同步 | 未通过 | 多次调整 `/0` 延迟、`/t` 重试、`/o` 重试仍未解决 |
| 2026-07-15 | 播放/暂停重复切封面 | 未修复 | 待后续分析 `MediaService` 事件触发来源 |
| 2026-07-15 | 本轮迭代实现 | 待验证 | P1/P2/静默启动已实现；P0 增强诊断与延迟，等待实机测试 |
| 2026-07-16 | 封面切换与摇臂控制 | 通过 | 取消 `/o` 重试、引入下位机 believed state 后，封面只落下 1 次，摇臂控制恢复正常 |
| 2026-07-16 | 偶发 AwaitingAck 超时提示 | 未修复 | 不影响实际封面显示，需配合下位机固件修复 `/a` 回包可靠性 |
| 2026-07-16 | 重连后首个视频封面 | 未修复 | 核心根因指向下位机 `pack_get_lost_counter` 未初始化，上位机侧已尽力兼容 |
| 2026-09-23 | 全面代码审查 + dotnet build | 通过 | 0 错误；修复前 27 警告，修复后 0 警告 |
| 2026-09-23 | 本轮质量修复（2.20-2.24） | 通过 | 启动/单实例、自动连接、封面同步、切换流畅度、播放暂停双向、拔插重连、退出托盘清理均符合预期 |
| 2026-09-23 | B站多次切换封面错位卡死 | 已修复 | 实机复现"上位机显示上一个视频封面"，根因 SMTC 事件数据滞后，2.25 防抖+主动重取修复 |
| 2026-09-23 | PotPlayer↔B站跨会话切换锁死 | 已修复 | 根因为 pending 机制取消传输后状态机不复位（2.16 遗留缺陷），2.27 修复 |
| 2026-09-23 | 封面切换延迟 | 可接受 | 2.26 两段式拉取（120ms+500ms）+ 16 包/Flush，用户确认延迟可接受、无掉初始化 |
| 2026-09-23 | 重连后首个视频封面 | 未修复（复现） | 3.5 按预期复现：首个封面同步失败，切下一视频恢复；固件问题，本轮不修 |
| 2026-09-24 | 「静默启动」可选设置 + 托盘双击呼出 | 编译通过 | 2.28：dotnet build 0 警告 0 错误；实机行为待验证（勾选后手动/自启均仅托盘，双击呼出） |
| 2026-09-24 | 托盘右键菜单两个开关项 | 编译通过 | 2.29：dotnet build 0 警告 0 错误；实机待验证（菜单勾选状态与窗口复选框双向一致） |
| 2026-09-25 | 2.28/2.29 实机验收 | 通过 | 用户确认验收 |
| 2026-09-25 | 3.7 停转根因定位 + 协议 v2 修复 | 编译通过 | 上位机 0 警告 0 错误；固件 GCC 构建通过（firmware.hex 已更新）；实机待烧录后验证 |

---

## 六、关键代码位置速查

- 串口帧解析 / 连接状态：`upper/Services/SerialPortService.cs`
- 图片传输状态机 / 命令处理：`upper/MainWindow.xaml.cs`
- 图片编码与分包：`upper/Services/ImageProcessor.cs`
- SMTC 媒体监听：`upper/Services/MediaService.cs`
- 下位机协议参考：`_firmware_temp/project/src/myAPP_usb.c`、`_firmware_temp/project/src/myAPP_display.c`

---

## 七、备注

- `_firmware_temp/` 为临时拉取的下位机固件源码，仅用于分析协议，**不应提交到本仓库**，已加入 `.gitignore`。
- 后续若继续调试，建议优先修复下位机固件中的 `pack_get_lost_counter` 变量未初始化问题，并考虑增加“ping”命令用于验证链路。

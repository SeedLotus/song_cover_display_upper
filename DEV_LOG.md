# 歌曲封面显示唱片机上位机开发日志

> 本文件记录 `song_cover_display_upper` 的开发修复、待修复问题、未来需求以及调试过程中的关键观察。  
> 维护者：Claude Code  
> 最后更新：2026-07-15

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

---

## 四、未来新需求

- 暂无新的未来需求；当前“开机自启动静默启动”已实现（启动参数 `--silent`）。

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

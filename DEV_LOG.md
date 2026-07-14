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

### 2.11 图片下落动画状态抑制

- **问题**：`/a` 后状态定时器 120ms 抑制太短，可能打断下位机图片下落动画。
- **修复**：抑制时间延长到 1000ms。

---

## 三、待修复问题

### 3.1 重连后封面仍无法同步（P0）

- **现象**：重新插拔 USB 后，上位机显示已连接、封面正确，但下位机不显示封面；摇臂功能正常。
- **观察**：
  - `/0`、`/1` 等状态命令能正常到达下位机（摇臂可控）。
  - `/t` 及后续图片包可能未被下位机正确处理。
- **可能根因**：
  1. 下位机 CDC 初始化完成后，`/t` 发送时机仍偏早，首次 `/t` 被丢弃。
  2. 下位机收到 `/t` 后发送 `/k` 时 `g_tx_completed` 为 false，`/k` 被静默丢弃，但 `flag_in_rx_pic` 已置 1；后续图片包若到达可正常处理，但若主机因未收到 `/k` 而状态机异常，可能导致流程中断。
  3. 图片包传输过程中丢包，下位机发送 `/r`/`/x` 请求重传，但主机未收到或重传逻辑未完全对齐。
  4. 下位机固件 `pack_get_lost_counter` 中 `uint8_t i` 未初始化，导致丢包统计错误，可能提前发送 `/a` 或误报大量丢包。
- **建议下一步**：
  - 在下位机固件中修复 `pack_get_lost_counter` 的 `i` 初始化。
  - 上位机增加更细粒度的诊断：记录每次 `/t`、`/k`、包发送进度、`/o`、`/a`、`/r`、`/x` 的时间戳与状态。
  - 考虑在下位机侧增加一个“ping/echo”测试命令，用于验证重连后主机到下位机的双向通路。

### 3.2 播放/暂停时封面重复切换（P1）

- **现象**：视频播放时会切一次封面，暂停时又会用当前封面再切一次。
- **可能根因**：`MediaService` 在播放状态变化时可能重新触发 `MediaPropertiesChanged`，导致 `ProcessAlbumArtAsync` 被重复调用；或者同一视频的不同状态触发了不同的 SMTC 会话/属性更新。
- **建议下一步**：
  - 在 `ProcessAlbumArtAsync` 中增加更严格的去重逻辑（不仅比较图片哈希，还要比较标题/艺术家组合）。
  - 检查 `OnPlaybackStateChanged` 是否会间接触发封面重新处理。
  - 增加日志记录每次进入 `ProcessAlbumArtAsync` 的调用来源。

### 3.3 切换视频封面延迟仍可优化（P2）

- **现象**：切换视频后封面更换仍有约 1 秒延迟。
- **已尝试**：缩短 `/k` 重试间隔到 150ms，`/a` 超时到 1000ms。
- **仍可优化方向**：
  - 图片传输阶段：目前每包 `Write + Flush`，可改为每 N 包刷新一次，或依赖 USB NAK 流控并加入精确 pacing。
  - 封面处理：异步化图片缩放/编码，避免阻塞 UI 线程。
  - `/a` 等待：若下位机固件能快速确认，可进一步缩短。

---

## 四、未来新需求

### 4.1 开机自启动后静默启动

- **需求**：开启“开机自启动”后，程序启动时直接最小化到系统托盘，不显示主窗口。
- **涉及文件**：
  - `MainWindow.xaml.cs`：在 `MainWindow()` 或 `Window_Loaded` 中检测启动参数或配置，调用 `HideWindowToTray()`。
  - `AutoStartManager.cs`：注册启动项时传递 `--minimized` 或 `--tray` 参数。
  - `App.xaml.cs`：解析启动参数，决定窗口初始状态。
- **注意事项**：
  - 需要避免与单实例检测（`App.IsSecondInstance`）冲突。
  - 静默启动时仍需初始化媒体服务、串口服务、自动重连等后台逻辑。
  - 建议增加用户可配置项：是否开机静默启动。

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

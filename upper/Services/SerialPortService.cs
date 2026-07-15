using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace upper.Services
{
    /// <summary>
    /// 串口通信服务：负责USB虚拟串口设备的发现、连接和数据通信
    /// </summary>
    public class SerialPortService : IDisposable
    {
        // 配置常量
        private const int DEFAULT_BAUD_RATE = 115200; // 这个改多少都没影响，因为不是真要转串口
        private const int READ_BUFFER_SIZE = 4096;
        private const int PACKET_DATA_SIZE = 61;      // 每个包的数据部分大小
        private const int PACKET_TOTAL_SIZE = 64;     // 每个包的总大小（1包头 + 2序号 + 61数据）

        // 核心对象
        private SerialPort? _serialPort;
        private readonly object _serialLock = new object();
        private bool _disposed = false;

        // 接收帧缓冲区
        private readonly List<byte> _receiveBuffer = new();
        private readonly object _bufferLock = new();
        private const int MAX_RECEIVE_BUFFER_SIZE = 1024;

        // 连接状态
        private bool _isConnected = false;
        private string? _connectedPort;

        // WMI 设备插拔监听
        private ManagementEventWatcher? _deviceRemovedWatcher;
        private ManagementEventWatcher? _deviceCreatedWatcher;
        private string _watchVid = "2E3C";
        private string _watchPid = "2568";

        // 公开的事件
        public event EventHandler<bool>? ConnectionStateChanged;
        public event EventHandler<string>? StatusMessage;
        public event EventHandler<DataReceivedEventArgs>? DataReceived;
        public event EventHandler<ImagePacketEventArgs>? ImagePacketSent;
        public event EventHandler<SerialCommandReceivedEventArgs>? SerialCommandReceived;

        // 事件参数类
        public class DataReceivedEventArgs : EventArgs
        {
            public byte[] RawData { get; set; } = Array.Empty<byte>();
            public string AsciiString { get; set; } = string.Empty;
            public DateTime ReceivedTime { get; set; } = DateTime.Now;
        }

        public class ImagePacketEventArgs : EventArgs
        {
            public int PacketIndex { get; set; }
            public int TotalPackets { get; set; }
            public int DataStartIndex { get; set; }
            public int DataLength { get; set; }
            public bool Success { get; set; }
            public string? ErrorMessage { get; set; }
        }

        // 结构化串口命令事件参数
        public class SerialCommandReceivedEventArgs : EventArgs
        {
            /// <summary>命令文本，例如 "/q1"、"/k"、"/r1234"</summary>
            public string CommandText { get; set; } = string.Empty;

            /// <summary>命令类型字符，即 '/' 后的第一个字符</summary>
            public char CommandType { get; set; }

            /// <summary>/r 或 /x 命令携带的包序号</summary>
            public ushort PacketIndex { get; set; }

            /// <summary>原始帧字节，包含结尾的 '\n'</summary>
            public byte[] RawFrame { get; set; } = Array.Empty<byte>();
        }

        // 属性
        public bool IsConnected => _serialPort?.IsOpen == true && _isConnected;
        public string? ConnectedPort => _connectedPort;
        public string? LastError { get; private set; }

        /// <summary>
        /// 构造函数
        /// </summary>
        public SerialPortService()
        {
            InitializeSerialPort();
        }

        /// <summary>
        /// 初始化串口对象。每次连接前重新创建，避免 USB 断开后句柄残留导致重连异常。
        /// 调用方需已持有 _serialLock。
        /// </summary>
        private void InitializeSerialPort()
        {
            if (_serialPort != null)
            {
                try
                {
                    if (_serialPort.IsOpen)
                    {
                        _serialPort.Close();
                    }
                }
                catch { }

                _serialPort.DataReceived -= SerialPort_DataReceived;
                _serialPort.ErrorReceived -= SerialPort_ErrorReceived;
                _serialPort.Dispose();
                _serialPort = null;
            }

            _serialPort = new SerialPort
            {
                BaudRate = DEFAULT_BAUD_RATE,
                Parity = Parity.None,
                DataBits = 8,
                StopBits = StopBits.One,
                Handshake = Handshake.None,
                ReadBufferSize = READ_BUFFER_SIZE,
                ReceivedBytesThreshold = 1,
                DtrEnable = true,
                RtsEnable = true,
                WriteTimeout = 2000,
                ReadTimeout = 500
            };

            _serialPort.DataReceived += SerialPort_DataReceived;
            _serialPort.ErrorReceived += SerialPort_ErrorReceived;
        }

        // ==================== 设备发现与连接 ====================

        /// 根据VID和PID查找当前连接的COM端口（增强版）
        /// </summary>
        /// <param name="vid">供应商ID（16进制，如"2E3C"）</param>
        /// <param name="pid">产品ID（16进制，如"2568"）</param>
        /// <returns>找到的、当前可用的COM端口列表</returns>
        public List<string> FindComPortsByVidPid(string vid, string pid)
        {
            var comports = new List<string>();
            // 构建正则表达式，匹配类似 VID_2E3C&PID_2568 的格式
            string pattern = $"^VID_{vid}.PID_{pid}";
            var rx = new Regex(pattern, RegexOptions.IgnoreCase);

            try
            {
                // --- 搜索路径1: 标准设备枚举路径 (主要路径) ---
                SearchRegistryForComPorts(@"SYSTEM\CurrentControlSet\Enum", rx, comports);

                // --- 搜索路径2: COM端口仲裁器路径 (用于某些USB串口设备) ---
                // 这个路径下存储了当前活动的COM端口映射，更直接。
                using (RegistryKey? comKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\COM Name Arbiter\Devices"))
                {
                    if (comKey != null)
                    {
                        // 这个键值下，值是COM端口名（如COM3），键名是设备实例路径，其中包含VID&PID
                        foreach (string deviceInstanceId in comKey.GetValueNames())
                        {
                            if (rx.IsMatch(deviceInstanceId))
                            {
                                object? portNameValue = comKey.GetValue(deviceInstanceId);
                                if (portNameValue != null)
                                {
                                    string portName = portNameValue.ToString()!;
                                    // 去重添加
                                    if (!comports.Contains(portName))
                                    {
                                        comports.Add(portName);
                                    }
                                }
                            }
                        }
                    }
                }

                OnStatusMessage($"初步找到 {comports.Count} 个匹配端口: {string.Join(", ", comports)}");

                // --- 关键步骤：验证端口当前是否真正可用 ---
                // 仅存在于注册表不代表物理设备一定连接着。
                // 这里尝试过滤掉可能已被占用或不存在的端口。
                var availablePorts = new List<string>();
                foreach (var port in comports)
                {
                    if (IsPortAvailable(port))
                    {
                        availablePorts.Add(port);
                    }
                    else
                    {
                        OnStatusMessage($"端口 {port} 在列表中但当前不可用，已过滤。");
                    }
                }

                return availablePorts;
            }
            catch (Exception ex)
            {
                OnStatusMessage($"查询注册表时发生异常: {ex.Message}");
                return comports; // 返回已找到的部分，或空列表
            }
        }

        /// <summary>
        /// 在指定的注册表根路径下递归搜索匹配的设备
        /// </summary>
        private void SearchRegistryForComPorts(string basePath, Regex vidPidRegex, List<string> foundPorts)
        {
            try
            {
                using (RegistryKey? baseKey = Registry.LocalMachine.OpenSubKey(basePath))
                {
                    if (baseKey == null) return;

                    // 遍历第一层子键（通常是设备大类，如USB）
                    foreach (string subKeyName in baseKey.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey? subKey = baseKey.OpenSubKey(subKeyName))
                            {
                                if (subKey == null) continue;

                                // 遍历第二层子键（具体的设备实例ID）
                                foreach (string deviceKeyName in subKey.GetSubKeyNames())
                                {
                                    // 检查设备实例ID是否匹配VID&PID
                                    if (!vidPidRegex.IsMatch(deviceKeyName)) continue;

                                    // 找到匹配设备，深入查找其“Device Parameters”下的PortName
                                    using (RegistryKey? deviceKey = subKey.OpenSubKey(deviceKeyName))
                                    {
                                        if (deviceKey == null) continue;

                                        // 一个物理设备可能有多个逻辑实例（如不同的接口）
                                        foreach (string instanceKeyName in deviceKey.GetSubKeyNames())
                                        {
                                            try
                                            {
                                                using (RegistryKey? instanceKey = deviceKey.OpenSubKey(instanceKeyName))
                                                using (RegistryKey? deviceParams = instanceKey?.OpenSubKey("Device Parameters"))
                                                {
                                                    object? portNameValue = deviceParams?.GetValue("PortName");
                                                    if (portNameValue != null)
                                                    {
                                                        string portName = portNameValue.ToString()!;
                                                        if (!foundPorts.Contains(portName))
                                                        {
                                                            foundPorts.Add(portName);
                                                        }
                                                    }
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                // 忽略单个实例访问错误，继续查找其他
                                                System.Diagnostics.Debug.WriteLine($"访问实例 {instanceKeyName} 时出错: {ex.Message}");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // 忽略单个子键访问错误，继续查找其他
                            System.Diagnostics.Debug.WriteLine($"访问子键 {subKeyName} 时出错: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OnStatusMessage($"搜索路径 {basePath} 时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 验证串口是否真的可以打开（即设备已连接且未被占用）
        /// </summary>
        private bool IsPortAvailable(string portName)
        {
            // 最简单直接的方法：尝试用 SerialPort 打开再关闭。
            // 注意：这只是为了验证存在性，不是真正的连接。
            using (var testPort = new SerialPort(portName))
            {
                try
                {
                    testPort.Open();
                    // 如果能成功打开，说明端口存在且当前空闲
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    // 端口存在，但被其他程序占用
                    return false;
                }
                catch (Exception)
                {
                    // 其他异常（如端口不存在）
                    return false;
                }
            }
        }

        /// <summary>
        /// 自动连接设备（按默认VID/PID）
        /// </summary>
        /// <param name="vid">供应商ID，默认为"2E3C"（Artery）</param>
        /// <param name="pid">产品ID，默认为"2568"</param>
        /// <returns>连接是否成功</returns>
        public bool AutoConnect(string vid = "2E3C", string pid = "2568")
        {
            try
            {
                OnStatusMessage($"正在查找设备 VID:{vid}, PID:{pid}...");

                var ports = FindComPortsByVidPid(vid, pid);
                if (ports.Count == 0)
                {
                    LastError = $"未找到目标设备 (VID: {vid}, PID: {pid})";
                    OnStatusMessage(LastError);
                    UpdateConnectionState(false);
                    return false;
                }

                // 尝试连接找到的第一个端口
                string targetPort = ports.First();
                return Connect(targetPort);
            }
            catch (Exception ex)
            {
                LastError = $"自动连接失败: {ex.Message}";
                OnStatusMessage(LastError);
                UpdateConnectionState(false);
                return false;
            }
        }

        /// <summary>
        /// 连接到指定串口
        /// </summary>
        /// <param name="portName">COM端口名（如"COM3"）</param>
        /// <param name="baudRate">波特率，默认为115200</param>
        /// <returns>连接是否成功</returns>
        public bool Connect(string portName, int baudRate = DEFAULT_BAUD_RATE)
        {
            if (_serialPort == null)
            {
                LastError = "串口对象未初始化";
                return false;
            }

            lock (_serialLock)
            {
                try
                {
                    // 先清理旧对象，防止 USB 重连后句柄残留
                    InitializeSerialPort();

                    // 配置串口参数
                    _serialPort.PortName = portName;
                    _serialPort.BaudRate = baudRate;

                    // 尝试打开串口
                    _serialPort.Open();

                    _connectedPort = portName;
                    LastError = null;

                    OnStatusMessage($"已连接到 {portName} (波特率: {baudRate})");
                    UpdateConnectionState(true);

                    // 启动 WMI 插拔监听，确保拔出后能立即检测到断开
                    StartDeviceWatchers("2E3C", "2568");

                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    LastError = $"端口 {portName} 被其他程序占用";
                    OnStatusMessage($"错误: {LastError}，请关闭其他串口软件");
                    UpdateConnectionState(false);
                    return false;
                }
                catch (Exception ex)
                {
                    LastError = $"连接失败: {ex.Message}";
                    OnStatusMessage($"连接异常: {ex.GetType().Name}: {ex.Message}");
                    UpdateConnectionState(false);
                    return false;
                }
            }
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            lock (_serialLock)
            {
                try
                {
                    if (_serialPort?.IsOpen == true)
                    {
                        _serialPort.Close();
                        OnStatusMessage("串口连接已关闭");
                    }
                }
                catch (Exception ex)
                {
                    OnStatusMessage($"断开连接时出错: {ex.Message}");
                }
                finally
                {
                    UpdateConnectionState(false);

                    // 停止 WMI 监听，避免断开后还收到旧设备事件
                    StopDeviceWatchers();
                }
            }
        }

        /// <summary>
        /// 启动 WMI 设备插拔监听
        /// </summary>
        public void StartDeviceWatchers(string vid, string pid)
        {
            StopDeviceWatchers();

            _watchVid = vid;
            _watchPid = pid;

            try
            {
                // 设备移除监听
                // WQL LIKE 中反斜杠是转义字符，用 % 通配符匹配 DeviceID 中的反斜杠
                string removedQuery = $@"SELECT * FROM __InstanceDeletionEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_PnPEntity' AND TargetInstance.DeviceID LIKE 'USB%VID_{vid}%PID_{pid}%'";
                _deviceRemovedWatcher = new ManagementEventWatcher(removedQuery);
                _deviceRemovedWatcher.EventArrived += (s, e) =>
                {
                    OnStatusMessage($"WMI 检测到设备移除 VID:{vid} PID:{pid}");
                    Disconnect();
                };
                _deviceRemovedWatcher.Start();

                // 设备插入监听
                string createdQuery = $@"SELECT * FROM __InstanceCreationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_PnPEntity' AND TargetInstance.DeviceID LIKE 'USB%VID_{vid}%PID_{pid}%'";
                _deviceCreatedWatcher = new ManagementEventWatcher(createdQuery);
                _deviceCreatedWatcher.EventArrived += (s, e) =>
                {
                    OnStatusMessage($"WMI 检测到设备插入 VID:{vid} PID:{pid}");
                    if (!IsConnected)
                    {
                        AutoConnect(vid, pid);
                    }
                };
                _deviceCreatedWatcher.Start();
            }
            catch (Exception ex)
            {
                OnStatusMessage($"启动 WMI 监听失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止 WMI 设备插拔监听
        /// </summary>
        public void StopDeviceWatchers()
        {
            try
            {
                _deviceRemovedWatcher?.Stop();
                _deviceRemovedWatcher?.Dispose();
                _deviceRemovedWatcher = null;

                _deviceCreatedWatcher?.Stop();
                _deviceCreatedWatcher?.Dispose();
                _deviceCreatedWatcher = null;
            }
            catch (Exception ex)
            {
                OnStatusMessage($"停止 WMI 监听失败: {ex.Message}");
            }
        }

        // ==================== 数据发送 ====================

        /// <summary>
        /// 发送字符串数据
        /// </summary>
        /// <param name="data">要发送的字符串</param>
        /// <returns>发送是否成功</returns>
        public bool SendString(string data)
        {
            if (!_isConnected || _serialPort == null)
            {
                OnStatusMessage("发送失败: 串口未连接");
                return false;
            }

            lock (_serialLock)
            {
                try
                {
                    _serialPort.Write(data);
                    _serialPort.BaseStream.Flush();
                    OnStatusMessage($"已发送: {FormatForDisplay(data)}");
                    return true;
                }
                catch (Exception ex)
                {
                    OnStatusMessage($"发送失败: {ex.Message}");

                    // 任何发送失败都视为连接异常，自动断开以便重连
                    Disconnect();

                    return false;
                }
            }
        }

        /// <summary>
        /// 发送字节数组数据
        /// </summary>
        /// <param name="data">要发送的字节数组</param>
        /// <returns>发送是否成功</returns>
        public bool SendBytes(byte[] data)
        {
            if (!_isConnected || _serialPort == null)
            {
                OnStatusMessage("发送失败: 串口未连接");
                return false;
            }

            lock (_serialLock)
            {
                try
                {
                    _serialPort.Write(data, 0, data.Length);
                    _serialPort.BaseStream.Flush();
                    OnStatusMessage($"已发送 {data.Length} 字节数据");
                    return true;
                }
                catch (Exception ex)
                {
                    OnStatusMessage($"发送失败: {ex.Message}");

                    // 任何发送失败都视为连接异常，自动断开以便重连
                    Disconnect();

                    return false;
                }
            }
        }

        /// <summary>
        /// 发送单个图片数据包
        /// </summary>
        /// <param name="packetIndex">包序号（从0开始）</param>
        /// <param name="imageData">完整的图片数据</param>
        /// <param name="dataStartIndex">数据起始索引</param>
        /// <param name="flush">发送后是否立即刷新底层流</param>
        /// <returns>发送是否成功</returns>
        public bool SendImagePacket(int packetIndex, byte[] imageData, int dataStartIndex = -1, bool flush = true)
        {
            if (!_isConnected || _serialPort == null)
            {
                OnStatusMessage("发送失败: 串口未连接");
                return false;
            }

            if (imageData == null || imageData.Length == 0)
            {
                OnStatusMessage("发送失败: 图片数据为空");
                return false;
            }

            lock (_serialLock)
            {
                try
                {
                    // 计算数据起始位置
                    int actualDataStartIndex = dataStartIndex >= 0
                        ? dataStartIndex
                        : packetIndex * PACKET_DATA_SIZE;

                    // 计算要发送的数据长度
                    int bytesToCopy = Math.Min(PACKET_DATA_SIZE, imageData.Length - actualDataStartIndex);

                    if (bytesToCopy <= 0)
                    {
                        OnStatusMessage($"发送失败: 数据索引越界 (包{packetIndex}, 起始{actualDataStartIndex})");
                        return false;
                    }

                    // 构建数据包
                    byte[] packet = new byte[PACKET_TOTAL_SIZE];

                    // 包头：'#' (0x23)
                    packet[0] = (byte)'#';

                    // 包序号（大端序：高字节在前，低字节在后）
                    packet[1] = (byte)((packetIndex >> 8) & 0xFF);  // 高字节
                    packet[2] = (byte)(packetIndex & 0xFF);         // 低字节

                    // 复制图片数据
                    Array.Copy(imageData, actualDataStartIndex, packet, 3, bytesToCopy);

                    // 发送数据包
                    _serialPort.Write(packet, 0, PACKET_TOTAL_SIZE);
                    if (flush)
                    {
                        _serialPort.BaseStream.Flush();
                    }

                    // 触发事件
                    OnImagePacketSent(new ImagePacketEventArgs
                    {
                        PacketIndex = packetIndex,
                        TotalPackets = CalculatePacketCount(imageData),
                        DataStartIndex = actualDataStartIndex,
                        DataLength = bytesToCopy,
                        Success = true
                    });

                    return true;
                }
                catch (Exception ex)
                {
                    OnImagePacketSent(new ImagePacketEventArgs
                    {
                        PacketIndex = packetIndex,
                        Success = false,
                        ErrorMessage = ex.Message
                    });

                    OnStatusMessage($"发送包 {packetIndex} 失败: {ex.Message}");

                    // 发送失败时自动断开
                    if (ex is System.IO.IOException || ex is InvalidOperationException)
                    {
                        Disconnect();
                    }

                    return false;
                }
            }
        }

        /// <summary>
        /// 批量发送一段连续的图片数据包，减少逐包 Flush 开销
        /// </summary>
        /// <param name="startIndex">起始包序号（从0开始）</param>
        /// <param name="count">要发送的包数量</param>
        /// <param name="imageData">完整的图片数据</param>
        /// <param name="flushEvery">每隔多少包刷新一次底层流，默认8</param>
        /// <returns>是否全部发送成功</returns>
        public bool SendImagePacketRange(int startIndex, int count, byte[] imageData, int flushEvery = 8)
        {
            if (!_isConnected || _serialPort == null)
            {
                OnStatusMessage("批量发送失败: 串口未连接");
                return false;
            }

            if (imageData == null || imageData.Length == 0 || count <= 0)
            {
                OnStatusMessage("批量发送失败: 参数无效");
                return false;
            }

            int totalPackets = CalculatePacketCount(imageData);
            int endIndex = Math.Min(startIndex + count, totalPackets);

            for (int i = startIndex; i < endIndex; i++)
            {
                bool isFlush = (i - startIndex + 1) % flushEvery == 0 || i == endIndex - 1;
                if (!SendImagePacket(i, imageData, -1, isFlush))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 发送播放状态命令
        /// </summary>
        /// <param name="isPlaying">是否正在播放</param>
        /// <returns>发送是否成功</returns>
        public bool SendPlaybackStatus(bool isPlaying)
        {
            string command = isPlaying ? "/1" : "/0";
            return SendString(command);
        }

        /// <summary>
        /// 发送图片传输开始命令
        /// </summary>
        /// <returns>发送是否成功</returns>
        public bool SendImageStartCommand()
        {
            return SendString("/t");
        }

        /// <summary>
        /// 发送图片传输结束命令
        /// </summary>
        /// <returns>发送是否成功</returns>
        public bool SendImageEndCommand()
        {
            return SendString("/o");
        }

        // ==================== 数据接收 ====================

        /// <summary>
        /// 串口数据接收事件处理
        /// </summary>
        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort == null || !_serialPort.IsOpen) return;

            try
            {
                // 读取所有可用字节
                int bytesToRead = _serialPort.BytesToRead;
                if (bytesToRead <= 0) return;

                byte[] buffer = new byte[bytesToRead];
                int bytesRead = _serialPort.Read(buffer, 0, bytesToRead);
                if (bytesRead <= 0) return;

                var rawData = buffer.Take(bytesRead).ToArray();

                // 保留原始调试事件
                OnDataReceived(new DataReceivedEventArgs
                {
                    RawData = rawData,
                    ReceivedTime = DateTime.Now
                });

                // 追加到接收缓冲区并尝试解析完整帧
                lock (_bufferLock)
                {
                    _receiveBuffer.AddRange(rawData);
                    ProcessCommandBuffer();
                }
            }
            catch (Exception ex)
            {
                OnStatusMessage($"接收数据出错: {ex.Message}");
                // 任何接收异常都视为连接异常，自动断开以便重连
                Disconnect();
            }
        }

        /// <summary>
        /// 清空接收缓冲区，通常在重连后使用，丢弃旧会话残留数据
        /// </summary>
        public void ClearReceiveBuffer()
        {
            lock (_bufferLock)
            {
                _receiveBuffer.Clear();
                OnStatusMessage("接收缓冲区已清空");
            }
        }

        /// <summary>
        /// 处理接收缓冲区：按 '\n' 拆分完整帧并解析命令
        /// </summary>
        private void ProcessCommandBuffer()
        {
            while (true)
            {
                int nlIndex = _receiveBuffer.IndexOf((byte)'\n');
                if (nlIndex < 0) break;

                byte[] frame = _receiveBuffer.Take(nlIndex + 1).ToArray();
                _receiveBuffer.RemoveRange(0, nlIndex + 1);

                // 仅处理以 '/' 开头的命令帧
                if (frame.Length >= 2 && frame[0] == (byte)'/')
                {
                    ParseAndRaiseCommand(frame);
                }
            }

            // 防止异常数据无限堆积
            if (_receiveBuffer.Count > MAX_RECEIVE_BUFFER_SIZE)
            {
                _receiveBuffer.Clear();
                OnStatusMessage("接收缓冲区超出安全上限，已清空");
            }
        }

        /// <summary>
        /// 解析单条命令帧并触发结构化事件
        /// </summary>
        private void ParseAndRaiseCommand(byte[] frame)
        {
            var args = new SerialCommandReceivedEventArgs
            {
                RawFrame = frame,
                CommandType = (char)frame[1]
            };

            switch (args.CommandType)
            {
                case 'q':
                    if (frame.Length >= 3 && (frame[2] == (byte)'0' || frame[2] == (byte)'1'))
                    {
                        args.CommandText = $"/q{(char)frame[2]}";
                    }
                    break;

                case 'k':
                    args.CommandText = "/k";
                    break;

                case 'a':
                    args.CommandText = "/a";
                    break;

                case 'r':
                case 'x':
                    if (frame.Length >= 5)
                    {
                        args.PacketIndex = (ushort)((frame[2] << 8) | frame[3]);
                        args.CommandText = $"/{args.CommandType}{args.PacketIndex}";
                    }
                    break;

                case 'e':
                    if (frame.Length >= 3)
                    {
                        args.CommandText = $"/e{(char)frame[2]}";
                    }
                    break;

                default:
                    args.CommandText = $"/{args.CommandType}";
                    break;
            }

            if (!string.IsNullOrEmpty(args.CommandText))
            {
                OnSerialCommandReceived(args);
            }
        }

        /// <summary>
        /// 串口错误事件处理
        /// </summary>
        private void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            string errorMessage = e.EventType switch
            {
                SerialError.RXOver => "接收缓冲区溢出",
                SerialError.Overrun => "数据溢出错误",
                SerialError.RXParity => "奇偶校验错误",
                SerialError.Frame => "帧错误",
                SerialError.TXFull => "发送缓冲区满",
                _ => $"未知串口错误: {e.EventType}"
            };

            OnStatusMessage($"串口错误: {errorMessage}");

            // 严重错误时自动断开，让重连逻辑接管
            if (e.EventType == SerialError.RXOver ||
                e.EventType == SerialError.Overrun ||
                e.EventType == SerialError.Frame)
            {
                Disconnect();
            }
        }

        // ==================== 辅助方法 ====================

        /// <summary>
        /// 格式化字符串用于显示（转义控制字符）
        /// </summary>
        private string FormatForDisplay(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            return input
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t")
                .Replace("\0", "\\0");
        }

        /// <summary>
        /// 计算图片数据的分包数量
        /// </summary>
        public static int CalculatePacketCount(byte[] imageData, int packetDataSize = PACKET_DATA_SIZE)
        {
            if (imageData == null || imageData.Length == 0) return 0;

            // 与下位机固件 PIC_PACK_NUMS (59 * 32 = 1888) 保持一致
            const int MAX_PACKET_COUNT = 59 * 32;
            int count = (int)Math.Ceiling(imageData.Length / (double)packetDataSize);
            return Math.Min(count, MAX_PACKET_COUNT);
        }

        // ==================== 事件触发方法 ====================

        protected virtual void OnConnectionStateChanged(bool isConnected)
        {
            ConnectionStateChanged?.Invoke(this, isConnected);
        }

        protected virtual void OnStatusMessage(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[Serial] {DateTime.Now:HH:mm:ss.fff} {message}");
            StatusMessage?.Invoke(this, message);
        }

        protected virtual void OnDataReceived(DataReceivedEventArgs e)
        {
            DataReceived?.Invoke(this, e);
        }

        protected virtual void OnImagePacketSent(ImagePacketEventArgs e)
        {
            ImagePacketSent?.Invoke(this, e);
        }

        protected virtual void OnSerialCommandReceived(SerialCommandReceivedEventArgs e)
        {
            SerialCommandReceived?.Invoke(this, e);
        }

        private void UpdateConnectionState(bool isConnected)
        {
            if (_isConnected != isConnected)
            {
                _isConnected = isConnected;
                if (!isConnected)
                {
                    _connectedPort = null;
                }
                OnConnectionStateChanged(isConnected);
            }
        }

        // ==================== 资源清理 ====================

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // 清理托管资源
                    Disconnect();

                    if (_serialPort != null)
                    {
                        _serialPort.DataReceived -= SerialPort_DataReceived;
                        _serialPort.ErrorReceived -= SerialPort_ErrorReceived;
                        _serialPort.Dispose();
                    }
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        ~SerialPortService()
        {
            Dispose(disposing: false);
        }
    }
}

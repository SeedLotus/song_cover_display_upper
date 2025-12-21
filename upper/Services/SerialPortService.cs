using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
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

        // 连接状态
        private bool _isConnected = false;
        private string? _connectedPort;

        // 公开的事件
        public event EventHandler<bool>? ConnectionStateChanged;
        public event EventHandler<string>? StatusMessage;
        public event EventHandler<DataReceivedEventArgs>? DataReceived;
        public event EventHandler<ImagePacketEventArgs>? ImagePacketSent;

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

        // 属性
        public bool IsConnected => _isConnected;
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
        /// 初始化串口对象
        /// </summary>
        private void InitializeSerialPort()
        {
            _serialPort = new SerialPort
            {
                BaudRate = DEFAULT_BAUD_RATE,
                Parity = Parity.None,
                DataBits = 8,
                StopBits = StopBits.One,
                Handshake = Handshake.None,
                ReadBufferSize = READ_BUFFER_SIZE,
                ReceivedBytesThreshold = 1  // 收到1字节就触发事件
            };

            _serialPort.DataReceived += SerialPort_DataReceived;
            _serialPort.ErrorReceived += SerialPort_ErrorReceived;
        }

        // ==================== 设备发现与连接 ====================

        /// <summary>
        /// 根据VID和PID查找COM端口
        /// </summary>
        /// <param name="vid">供应商ID（16进制，如"2E3C"）</param>
        /// <param name="pid">产品ID（16进制，如"2568"）</param>
        /// <returns>找到的COM端口列表</returns>
        public List<string> FindComPortsByVidPid(string vid, string pid)
        {
            var comports = new List<string>();
            string pattern = $"^VID_{vid}.PID_{pid}";
            var rx = new Regex(pattern, RegexOptions.IgnoreCase);

            try
            {
                using (RegistryKey? baseKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum"))
                {
                    if (baseKey == null) return comports;

                    foreach (string deviceCategory in baseKey.GetSubKeyNames())
                    {
                        using (RegistryKey? categoryKey = baseKey.OpenSubKey(deviceCategory))
                        {
                            if (categoryKey == null) continue;

                            foreach (string deviceId in categoryKey.GetSubKeyNames())
                            {
                                if (!rx.Match(deviceId).Success) continue;

                                using (RegistryKey? deviceKey = categoryKey.OpenSubKey(deviceId))
                                {
                                    if (deviceKey == null) continue;

                                    foreach (string instance in deviceKey.GetSubKeyNames())
                                    {
                                        using (RegistryKey? instanceKey = deviceKey.OpenSubKey(instance))
                                        using (RegistryKey? deviceParams = instanceKey?.OpenSubKey("Device Parameters"))
                                        {
                                            object? portNameValue = deviceParams?.GetValue("PortName");
                                            if (portNameValue != null)
                                            {
                                                string portName = portNameValue.ToString()!;
                                                if (!comports.Contains(portName))
                                                    comports.Add(portName);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OnStatusMessage($"查询注册表出错: {ex.Message}");
            }

            return comports;
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
                    // 如果已经连接，先断开
                    if (_serialPort.IsOpen)
                    {
                        Disconnect();
                    }

                    // 配置串口参数
                    _serialPort.PortName = portName;
                    _serialPort.BaudRate = baudRate;

                    // 尝试打开串口
                    _serialPort.Open();

                    _isConnected = true;
                    _connectedPort = portName;
                    LastError = null;

                    OnStatusMessage($"已连接到 {portName} (波特率: {baudRate})");
                    UpdateConnectionState(true);

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

                    _isConnected = false;
                    _connectedPort = null;
                    UpdateConnectionState(false);
                }
                catch (Exception ex)
                {
                    OnStatusMessage($"断开连接时出错: {ex.Message}");
                }
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
                    OnStatusMessage($"已发送: {FormatForDisplay(data)}");
                    return true;
                }
                catch (Exception ex)
                {
                    OnStatusMessage($"发送失败: {ex.Message}");

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
                    OnStatusMessage($"已发送 {data.Length} 字节数据");
                    return true;
                }
                catch (Exception ex)
                {
                    OnStatusMessage($"发送失败: {ex.Message}");

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
        /// 发送单个图片数据包
        /// </summary>
        /// <param name="packetIndex">包序号（从0开始）</param>
        /// <param name="imageData">完整的图片数据</param>
        /// <param name="dataStartIndex">数据起始索引</param>
        /// <returns>发送是否成功</returns>
        public bool SendImagePacket(int packetIndex, byte[] imageData, int dataStartIndex = -1)
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

                    // 触发事件
                    OnImagePacketSent(new ImagePacketEventArgs
                    {
                        PacketIndex = packetIndex,
                        TotalPackets = (int)Math.Ceiling(imageData.Length / (double)PACKET_DATA_SIZE),
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

                // 处理接收到的数据
                ProcessReceivedData(buffer, bytesRead);
            }
            catch (Exception ex)
            {
                OnStatusMessage($"接收数据出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理接收到的数据
        /// </summary>
        private void ProcessReceivedData(byte[] data, int length)
        {
            if (length <= 0) return;

            string asciiString = Encoding.ASCII.GetString(data, 0, length);

            // 触发数据接收事件
            OnDataReceived(new DataReceivedEventArgs
            {
                RawData = data.Take(length).ToArray(),
                AsciiString = asciiString,
                ReceivedTime = DateTime.Now
            });

            // 自动检测常见命令（可选，主窗口也可以处理）
            AutoDetectCommands(asciiString);
        }

        /// <summary>
        /// 自动检测常见命令（供调试用）
        /// </summary>
        private void AutoDetectCommands(string data)
        {
            if (data.Contains("/k"))
            {
                OnStatusMessage("收到下位机就绪响应: /k");
            }
            else if (data.Contains("/r"))
            {
                OnStatusMessage("收到重传单个包请求: /r");
            }
            else if (data.Contains("/x"))
            {
                OnStatusMessage("收到重传后续包请求: /x");
            }
            else if (data.Contains("/q0") || data.Contains("/q1"))
            {
                OnStatusMessage($"收到播放控制命令: {data.Trim()}");
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
            return (int)Math.Ceiling(imageData.Length / (double)packetDataSize);
        }

        // ==================== 事件触发方法 ====================

        protected virtual void OnConnectionStateChanged(bool isConnected)
        {
            ConnectionStateChanged?.Invoke(this, isConnected);
        }

        protected virtual void OnStatusMessage(string message)
        {
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

        private void UpdateConnectionState(bool isConnected)
        {
            //if (_isConnected != isConnected)
            //{
                //_isConnected = isConnected;
                OnConnectionStateChanged(isConnected);
            //}
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

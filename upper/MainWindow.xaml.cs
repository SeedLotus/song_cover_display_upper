using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using upper.Services;

namespace upper
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // ==================== 服务实例声明 ====================
        private readonly MediaService _mediaService; // 系统媒体
        private readonly TrayService _trayService; // 托盘化
        private AutoStartManager _autoStartManager; // 开机自启动
        private readonly SerialPortService _serialPortService; // 串口和下位机控制，ai 没把这两个解耦，就这样吧


        // ==================== 状态管理字段 ====================
        private string? _currentPlayStatus;           // 当前播放状态
        private byte[]? _currentImageRgb565Data;      // 当前图片的RGB565编码数据
        private string? _lastImageHash;               // 上次图片哈希（用于变化检测）
        private string _lastMediaTitle = string.Empty;   // 上次媒体标题（用于播放/暂停去重）
        private string _lastMediaArtist = string.Empty;  // 上次媒体艺术家
        private string _lastMediaAlbum = string.Empty;   // 上次媒体专辑
        private bool _isExitingFromTrayMenu = false;  // 是否从托盘菜单退出
        private DispatcherTimer _statusSendTimer;     // 定时发送播放状态到串口
        private DispatcherTimer _autoReconnectTimer;  // 自动重连定时器

        // 图片传输状态机
        private enum ImageTransferPhase
        {
            Idle,           // 空闲
            Starting,       // /t 已发送，等待 /k
            Transferring,   // 正在发送图片包
            AwaitingAck,    // /o 已发送，等待 /a
            Completed       // /a 已收到
        }

        private ImageTransferPhase _imagePhase = ImageTransferPhase.Idle;
        private byte[]? _imageDataBeingSent;          // 当前正在发送的图片数据
        private byte[]? _pendingImageData;            // 排队等待发送的最新图片数据
        private ulong _transferId = 0;                // 传输代 ID，递增
        private readonly object _imageTransferLock = new();
        private CancellationTokenSource? _imageTransferCts;
        private DateTime _suppressStatusUntil = DateTime.MinValue; // 抑制 /1 发送的时间点

        // 重连后封面同步的可配置参数
        private static readonly TimeSpan PostConnectSettleDelay = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan WakeCommandDelay = TimeSpan.FromSeconds(1.5);
        private static readonly TimeSpan LineQuietTimeout = TimeSpan.FromMilliseconds(300);
        private const int ReconnectKRetryCount = 12;
        private const int ReconnectKRetryIntervalMs = 250;

        // 串口接收时间跟踪（用于重连后线路安静检测）
        private DateTime _lastSerialReceiveTime = DateTime.MinValue;
        private readonly object _receiveTimeLock = new();


        public MainWindow()
        {
            InitializeComponent();

            // 初始化服务实例
            _mediaService = new MediaService();
            _trayService = new TrayService();
            _autoStartManager = new AutoStartManager();
            _serialPortService = new SerialPortService();


            // 初始化各模块
            InitializeMediaService();
            InitializeTrayService();
            InitializeAutoStart();
            InitializeSerialPortService();
            InitializeTimers();

            // 启动时尝试自动连接设备
            AutoConnectDevice();
        }

    // ==================== 服务初始化 ====================
        // 初始化媒体服务
        private async void InitializeMediaService()
        {
            try
            {
                // 订阅媒体服务事件
                _mediaService.MediaInfoChanged += OnMediaInfoChanged;
                _mediaService.PlaybackStateChanged += OnPlaybackStateChanged;
                _mediaService.SessionAvailabilityChanged += OnSessionAvailabilityChanged;

                // 异步初始化媒体服务
                await _mediaService.InitializeAsync();
            }
            catch (Exception ex)
            {
                //ControlStatusText.Text = "媒体服务初始化失败";
                //ControlStatusText.Foreground = System.Windows.Media.Brushes.Red;
            }
        }

        // 初始化托盘服务
        private void InitializeTrayService()
        {
            string iconPath = GetEmbeddedIconAsTempFile();
            if (!string.IsNullOrEmpty(iconPath))
            {
                // 可以用在 NotifyIcon 等需要文件路径的地方
                Icon? _app_icon = new System.Drawing.Icon(iconPath);

                _trayService.Initialize("唱片机控制器 - @realTiX", _app_icon);
                // 使用后清理临时文件（可选）
                // File.Delete(iconPath);
            }
            else
            {
                // 配置托盘服务
                _trayService.Initialize("唱片机控制器 - @realTiX");
            }

            // 订阅托盘事件
            _trayService.TrayIconLeftClick += (s, e) => RestoreWindowFromTray();
            _trayService.OpenUrlRequested += (s, e) => OpenWebsite();
            _trayService.ExitRequested += (s, e) =>
            {
                _isExitingFromTrayMenu = true;
                System.Windows.Application.Current.Shutdown();
            };
        }

        // 获取托盘图标
        public string GetEmbeddedIconAsTempFile()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string resourceName = "upper.Assets.icon.ico";
            string tempFilePath = Path.Combine(Path.GetTempPath(), "icon.ico");

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream != null)
                {
                    using (var fileStream = File.Create(tempFilePath))
                    {
                        stream.CopyTo(fileStream);
                    }
                    return tempFilePath;
                }
            }
            return null;
        }

        // 初始化开机自启动功能
        private void InitializeAutoStart()
        {
            try
            {
                // 检测当前状态
                bool isEnabled = _autoStartManager.CheckStatus();

                // 获取详细的EXE文件信息
                string exeInfo = _autoStartManager.GetExeFileInfo();

                // 更新UI
                Dispatcher.Invoke(() =>
                {
                    AutoStartCheckBox.IsChecked = isEnabled;
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    AutoStartCheckBox.IsEnabled = false;
                });
            }
        }

        /// 初始化串口服务
        private void InitializeSerialPortService()
        {
            // 订阅串口服务事件
            _serialPortService.ConnectionStateChanged += OnSerialConnectionChanged;
            _serialPortService.StatusMessage += OnSerialStatusMessage;
            _serialPortService.DataReceived += (s, e) =>
            {
                lock (_receiveTimeLock)
                {
                    _lastSerialReceiveTime = DateTime.Now;
                }
            };
            _serialPortService.SerialCommandReceived += OnSerialCommandReceived;
            _serialPortService.ImagePacketSent += OnImagePacketSent;
        }

        // 初始化定时器
        private void InitializeTimers()
        {
            // 播放状态发送定时器（每秒发送一次）
            _statusSendTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _statusSendTimer.Tick += StatusSendTimer_Tick;

            // 自动重连定时器（每5秒尝试一次）
            _autoReconnectTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5),
                IsEnabled = false // 默认不启用，链接失败后启用
            };
            _autoReconnectTimer.Tick += AutoReconnectTimer_Tick;
        }


    // ==================== 事件处理相关 ====================

        // ==================== 媒体事件处理 ====================
        // 媒体信息变化事件处理
        private async void OnMediaInfoChanged(object sender, MediaService.MediaInfoChangedEventArgs e)
        {
            // 在UI线程上更新媒体信息显示
            await Dispatcher.InvokeAsync(() =>
            {
                TitleTextBlock.Text = e.Title;
                ArtistTextBlock.Text = e.Artist;
                AlbumTextBlock.Text = e.Album;
            });

            // 媒体身份去重：同一歌曲在播放/暂停状态变化时，SMTC 可能重复触发 MediaPropertiesChanged，
            // 但标题/艺术家/专辑未变，此时跳过封面重新处理。
            bool identityChanged = e.Title != _lastMediaTitle
                                || e.Artist != _lastMediaArtist
                                || e.Album != _lastMediaAlbum;

            // 处理专辑封面（如果有）
            if (e.ThumbnailStream is Windows.Storage.Streams.IRandomAccessStreamReference thumbnailRef)
            {
                if (!identityChanged && _currentImageRgb565Data != null)
                {
                    // 同一媒体且已处理过封面，跳过
                    return;
                }

                _lastMediaTitle = e.Title;
                _lastMediaArtist = e.Artist;
                _lastMediaAlbum = e.Album;

                // 图片处理涉及大量 DispatcherObject，统一放到 UI 线程执行
                await Dispatcher.InvokeAsync(async () => await ProcessAlbumArtAsync(thumbnailRef));
            }
            else
            {
                _lastMediaTitle = e.Title;
                _lastMediaArtist = e.Artist;
                _lastMediaAlbum = e.Album;

                await Dispatcher.InvokeAsync(() =>
                {
                    // 显示默认占位图
                    AlbumArtImage.Source = ImageProcessor.CreatePlaceholderImage();
                    _currentImageRgb565Data = null;
                    _lastImageHash = null;
                });
            }
        }

        // 播放状态变化事件处理
        private void OnPlaybackStateChanged(object sender, MediaService.PlaybackStateChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                // 更新播放状态显示
                _currentPlayStatus = e.State;
                StatusTextBlock.Text = $"状态: {GetPlaybackStatusString(e.State)}";

                // 根据播放状态更新按钮可用性
                bool isPlaying = e.State == "Playing";
                SysPlayButton.IsEnabled = !isPlaying;
                SysPauseButton.IsEnabled = isPlaying;

                // 播放状态变化时立即通知下位机，而不是等 1 秒定时器
                if (_serialPortService.IsConnected)
                {
                    _serialPortService.SendPlaybackStatus(isPlaying);
                }
            });
        }

        // 媒体会话可用性变化事件处理
        private void OnSessionAvailabilityChanged(object sender, bool isAvailable)
        {
            Dispatcher.Invoke(() =>
            {
                //ControlStatusText.Text = isAvailable
                //    ? "√ 检测到可控制的媒体会话"
                //    : "❓ 未检测到可控制的媒体会话";
                //ControlStatusText.Foreground = isAvailable ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Gray;

                // 更新控制按钮状态
                SysPlayButton.IsEnabled = isAvailable;
                SysPauseButton.IsEnabled = isAvailable;

                if (!isAvailable)
                {
                    //ControlResultText.Text = "请先打开一个媒体播放器";
                    //ControlResultText.Foreground = System.Windows.Media.Brushes.DarkOrange;
                }
            });
        }

        // 转换播放状态为中文描述
        private string GetPlaybackStatusString(string status)
        {
            return status switch
            {
                "Playing" => "▶ 播放中",
                "Paused" => "⏸ 已暂停",
                "Stopped" => "⏹ 已停止", // 不知道 ai 在写什么，哪有后面两个状态
                "Changing" => "🔄 切换中",
                _ => status
            };
        }

        // ==================== 图片处理逻辑 ====================

        // 处理并显示专辑封面
        private async Task ProcessAlbumArtAsync(
            Windows.Storage.Streams.IRandomAccessStreamReference thumbnailRef)
        {
            try
            {
                // 加载原始图像
                using (var stream = await thumbnailRef.OpenReadAsync())
                {
                    var originalBitmap = new System.Windows.Media.Imaging.BitmapImage();
                    originalBitmap.BeginInit();
                    originalBitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    originalBitmap.StreamSource = stream.AsStream();
                    originalBitmap.EndInit();
                    originalBitmap.Freeze();

                    // 检测图片是否变化
                    string newHash = ImageProcessor.ComputeImageHash(originalBitmap);
                    if (newHash == _lastImageHash)
                    {
                        // 图片未变化，直接返回
                        return;
                    }

                    // 处理图片（智能缩放 + 模糊背景），使用后台优先级减少 UI 卡顿
                    var processedImage = await Dispatcher.InvokeAsync(
                        () => ImageProcessor.ProcessAlbumArt(originalBitmap),
                        DispatcherPriority.Background);
                    processedImage?.Freeze();

                    // 更新UI显示
                    await Dispatcher.InvokeAsync(() =>
                    {
                        AlbumArtImage.Source = processedImage;
                    });

                    // 编码为RGB565（用于发送到下位机），移到后台线程避免阻塞 UI
                    _currentImageRgb565Data = await Task.Run(() =>
                    {
                        if (processedImage == null) return null;
                        return ImageProcessor.ConvertToRgb565(processedImage);
                    });
                    _lastImageHash = newHash;

                    if (_currentImageRgb565Data == null)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            ImageTransferStatusText.Text = "封面编码失败";
                        });
                        return;
                    }

                    // 更新状态显示
                    await Dispatcher.InvokeAsync(() =>
                    {
                        int totalPackets = ImageProcessor.CalculatePacketCount(_currentImageRgb565Data);
                        ImageTransferStatusText.Text =
                            $"✅ 封面图片就绪 ({_currentImageRgb565Data.Length} 字节, {totalPackets} 包)";
                    });

                    // 启动图片传输状态机
                    StartImageTransfer(_currentImageRgb565Data);
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    AlbumArtImage.Source = ImageProcessor.CreatePlaceholderImage();
                    _currentImageRgb565Data = null;
                    _lastImageHash = null;
                    ImageTransferStatusText.Text = $"封面处理失败: {ex.Message}";
                });
            }
        }

        // ==================== 托盘事件处理 ====================

        // 从托盘恢复窗口
        public void RestoreWindowFromTray()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();

            // 临时置顶以确保窗口显示在前面
            this.Topmost = true;
            this.Topmost = false;
        }

        // 隐藏窗口到托盘
        private void HideWindowToTray()
        {
            this.Hide();
            //_trayService.ShowNotification("程序已最小化到托盘",
            //    "单击托盘图标可恢复窗口",
            //    System.Windows.Forms.ToolTipIcon.Info,
            //    1000);
        }

        private void OpenWebsite()
        {
            string url = "https://space.bilibili.com/40194368";

            try
            {
                // 使用系统默认浏览器打开URL
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true  // 使用系统Shell执行，这样会调用默认浏览器
                });
            }
            catch (Exception ex)
            {
                // 如果第一种方法失败，尝试另一种方法
                try
                {
                    System.Diagnostics.Process.Start(url);
                }
                catch (Exception ex2)
                {
                    string errorMessage = $"无法打开浏览器: {ex2.Message}";

                    // 显示错误消息
                    System.Windows.MessageBox.Show(errorMessage + $"\n\n请手动访问: {url}",
                        "浏览器打开失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    // 尝试复制URL到剪贴板，方便用户手动粘贴
                    try
                    {
                        System.Windows.Clipboard.SetText(url);
                    }
                    catch
                    {
                        // 剪贴板复制失败
                    }
                }
            }
        }


        // ==================== 串口事件处理 ====================

        // 串口连接状态变化事件处理
        private async void OnSerialConnectionChanged(object sender, bool isConnected)
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                // 更新连接状态指示灯
                StatusLed.Fill = isConnected ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Red;
                ConnectionStatusText.Text = isConnected
                    ? $"已连接到 {_serialPortService.ConnectedPort}"
                    : "设备未连接";

                // 更新按钮状态
                ConnectButton.IsEnabled = !isConnected;
                DisconnectButton.IsEnabled = isConnected;

                // 启动或停止状态发送定时器
                if (isConnected)
                {
                    _statusSendTimer.Start();
                    _autoReconnectTimer.IsEnabled = false; // 连接成功时关闭自动重连

                    // 重连后如果已有封面数据，执行更充分的握手序列再同步
                    if (_currentImageRgb565Data != null)
                    {
                        lock (_imageTransferLock)
                        {
                            _imageTransferCts?.Cancel();
                            _imagePhase = ImageTransferPhase.Idle;
                            _pendingImageData = null;
                        }

                        ImageTransferStatusText.Text = "连接成功，等待 USB/CDC 稳定...";
                        await Task.Delay(PostConnectSettleDelay);

                        if (!_serialPortService.IsConnected)
                        {
                            ImageTransferStatusText.Text = "同步前连接已断开，取消本次同步";
                            return;
                        }

                        // 丢弃旧会话残留数据
                        _serialPortService.ClearReceiveBuffer();

                        // 先发送 /0 唤醒并稳定下位机状态，等待唱臂状态机完成过渡
                        ImageTransferStatusText.Text = "发送 /0 唤醒下位机，等待稳定...";
                        _serialPortService.SendPlaybackStatus(false);
                        await Task.Delay(WakeCommandDelay);

                        if (!_serialPortService.IsConnected)
                        {
                            ImageTransferStatusText.Text = "同步前连接已断开，取消本次同步";
                            return;
                        }

                        // 再次清理缓冲区并等待线路安静
                        _serialPortService.ClearReceiveBuffer();
                        ImageTransferStatusText.Text = "等待串口线路安静...";
                        await WaitForLineQuietAsync(LineQuietTimeout, TimeSpan.FromSeconds(2), _imageTransferCts?.Token ?? default);

                        if (!_serialPortService.IsConnected)
                        {
                            ImageTransferStatusText.Text = "同步前连接已断开，取消本次同步";
                            return;
                        }

                        ImageTransferStatusText.Text = "开始同步封面到重连后的设备...";
                        StartImageTransfer(_currentImageRgb565Data, kRetryCount: ReconnectKRetryCount, kRetryIntervalMs: ReconnectKRetryIntervalMs);
                    }
                }
                else
                {
                    _statusSendTimer.Stop();
                    _autoReconnectTimer.IsEnabled = true; // 断开后启用自动重连
                }
            });
        }

        // 串口状态消息事件处理
        private void OnSerialStatusMessage(object sender, string message)
        {
            // 将串口状态消息显示在 UI 上，便于现场诊断
            Dispatcher.InvokeAsync(() =>
            {
                ImageTransferStatusText.Text = message;
            });
        }

        // 串口命令接收事件处理
        private void OnSerialCommandReceived(object sender, SerialPortService.SerialCommandReceivedEventArgs e)
        {
            Dispatcher.Invoke(() => ProcessSerialCommand(e));
        }

        // 处理串口接收到的结构化命令
        private void ProcessSerialCommand(SerialPortService.SerialCommandReceivedEventArgs e)
        {
            switch (e.CommandText)
            {
                case "/q1":
                    // 下位机请求播放命令
                    ImageTransferStatusText.Text = "收到 /q1，请求播放";
                    _serialPortService.SendPlaybackStatus(true);
                    _ = _mediaService.TryPlayAsync();
                    break;

                case "/q0":
                    // 下位机请求暂停命令
                    ImageTransferStatusText.Text = "收到 /q0，请求暂停";
                    _serialPortService.SendPlaybackStatus(false);
                    _ = _mediaService.TryPauseAsync();
                    break;

                case "/k":
                    // 下位机准备接收图片就绪响应
                    HandleImageTransferReady();
                    break;

                case "/a":
                    // 下位机成功接收所有包
                    HandleImageTransferAck();
                    break;

                default:
                    if (e.CommandType == 'r')
                    {
                        // 重传单个包请求
                        HandleResendSinglePacket(e.PacketIndex);
                    }
                    else if (e.CommandType == 'x')
                    {
                        // 重传后续包请求
                        HandleResendFromPacket(e.PacketIndex);
                    }
                    break;
            }
        }

        // ==================== 图片传输状态机 ====================

        // 启动一次新的图片传输
        private void StartImageTransfer(byte[] imageData, int kRetryCount = 2, int kRetryIntervalMs = 150)
        {
            ulong currentTransferId;

            lock (_imageTransferLock)
            {
                // 如果当前正在传输，先排队为 pending 并请求取消当前传输，
                // 等当前传输自然结束（收到 /a 或超时）后再启动新传输，
                // 避免向下位机发送重叠的 /t 命令。
                if (_imagePhase != ImageTransferPhase.Idle)
                {
                    _pendingImageData = imageData;
                    _imageTransferCts?.Cancel();
                    return;
                }

                _transferId++;
                currentTransferId = _transferId;
                _imageDataBeingSent = imageData;
                _pendingImageData = null;
                _imagePhase = ImageTransferPhase.Starting;
                _imageTransferCts = new CancellationTokenSource();
            }

            if (!_serialPortService.IsConnected)
            {
                lock (_imageTransferLock)
                {
                    _imagePhase = ImageTransferPhase.Idle;
                }
                ImageTransferStatusText.Text = "设备未连接，图片传输已挂起";
                return;
            }

            ImageTransferStatusText.Text = $"准备传输图片 (transfer {currentTransferId})，发送 /t 等待 /k...";
            _serialPortService.SendImageStartCommand();

            // /k 可能因下位机 TX 忙而丢失；主动重试 /t，若仍未收到 /k 则直接开始发图
            _ = WaitForKWithRetryAsync(currentTransferId, _imageTransferCts.Token, kRetryCount, kRetryIntervalMs);
        }

        // 收到 /k：下位机已准备好接收图片
        private void HandleImageTransferReady()
        {
            ulong transferId;
            byte[]? imageData;
            CancellationTokenSource? cts;

            lock (_imageTransferLock)
            {
                if (_imagePhase != ImageTransferPhase.Starting)
                {
                    ImageTransferStatusText.Text = $"收到 /k 但状态为 {_imagePhase}，忽略";
                    return;
                }

                transferId = _transferId;
                imageData = _imageDataBeingSent;
                cts = _imageTransferCts;
                _imagePhase = ImageTransferPhase.Transferring;
            }

            if (imageData == null) return;

            ImageTransferStatusText.Text = "收到 /k，开始发送图片包...";
            _ = SendImageAsync(transferId, imageData, cts?.Token ?? default);
        }

        // 实际发送图片包
        private async Task SendImageAsync(ulong transferId, byte[] imageData, CancellationToken ct)
        {
            try
            {
                int totalPackets = ImageProcessor.CalculatePacketCount(imageData);

                await Dispatcher.InvokeAsync(() =>
                {
                    ImageTransferStatusText.Text = $"开始传输图片，共 {totalPackets} 包...";
                });

                const int BATCH_SIZE = 8;
                for (int i = 0; i < totalPackets; i += BATCH_SIZE)
                {
                    ct.ThrowIfCancellationRequested();

                    lock (_imageTransferLock)
                    {
                        if (_transferId != transferId || _imagePhase != ImageTransferPhase.Transferring)
                            return;
                    }

                    int batchCount = Math.Min(BATCH_SIZE, totalPackets - i);
                    _serialPortService.SendImagePacketRange(i, batchCount, imageData, flushEvery: BATCH_SIZE);

                    // 每 64 包让出一次，避免阻塞 UI
                    if (i % 64 == 0)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            ImageTransferStatusText.Text =
                                $"发送进度: {Math.Min(i + BATCH_SIZE, totalPackets)}/{totalPackets} 包";
                        });
                        await Task.Yield();
                    }
                }

                lock (_imageTransferLock)
                {
                    if (_transferId != transferId || _imagePhase != ImageTransferPhase.Transferring)
                        return;

                    _imagePhase = ImageTransferPhase.AwaitingAck;
                }

                _serialPortService.SendImageEndCommand();

                // 1000ms 内未收到 /a 则超时，超时后会重试一次 /o
                _ = WaitForPhaseTimeoutAsync(transferId, ImageTransferPhase.AwaitingAck, 1000);
            }
            catch (OperationCanceledException)
            {
                // 正常取消，忽略
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ImageTransferStatusText.Text = $"发送图片失败: {ex.Message}";
                });

                lock (_imageTransferLock)
                {
                    _imagePhase = ImageTransferPhase.Idle;
                }
            }
        }

        // 收到 /a：图片完整接收成功
        private void HandleImageTransferAck()
        {
            byte[]? pending = null;

            lock (_imageTransferLock)
            {
                if (_imagePhase != ImageTransferPhase.AwaitingAck) return;

                _imagePhase = ImageTransferPhase.Completed;
                _imageTransferCts?.Cancel();

                // 抑制状态定时器约 1000ms，覆盖下位机图片下落动画，避免打断动画
                _suppressStatusUntil = DateTime.Now.AddMilliseconds(1000);

                pending = _pendingImageData;
                _pendingImageData = null;
            }

            ImageTransferStatusText.Text = "✅ 收到 /a，图片发送完成";

            // 图片到位后立即同步当前播放状态，确保摇臂位置与 PC 一致
            if (_serialPortService.IsConnected)
            {
                bool isPlaying = _currentPlayStatus == "Playing";
                _serialPortService.SendPlaybackStatus(isPlaying);
            }

            if (pending != null)
            {
                StartImageTransfer(pending);
            }
            else
            {
                lock (_imageTransferLock)
                {
                    _imagePhase = ImageTransferPhase.Idle;
                }
            }
        }

        // 传输阶段超时处理（仅用于 AwaitingAck/Completed 等非 Starting 阶段）
        private async Task WaitForPhaseTimeoutAsync(ulong transferId, ImageTransferPhase expectedPhase, int milliseconds, bool isRetry = false)
        {
            try
            {
                await Task.Delay(milliseconds, _imageTransferCts?.Token ?? default);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_imageTransferLock)
            {
                if (_transferId != transferId || _imagePhase != expectedPhase)
                    return;
            }

            // AwaitingAck 超时时，先尝试重发一次 /o（可能是 /o 或 /a 丢失）
            if (expectedPhase == ImageTransferPhase.AwaitingAck && !isRetry)
            {
                ImageTransferStatusText.Text = "/a 超时，重试发送 /o...";
                _serialPortService.SendImageEndCommand();
                _ = WaitForPhaseTimeoutAsync(transferId, expectedPhase, milliseconds, isRetry: true);
                return;
            }

            byte[]? pending = null;

            lock (_imageTransferLock)
            {
                if (_transferId != transferId || _imagePhase != expectedPhase)
                    return;

                _imagePhase = ImageTransferPhase.Idle;
                ImageTransferStatusText.Text = $"图片传输超时: {expectedPhase}";
                pending = _pendingImageData;
                _pendingImageData = null;
            }

            // 其他阶段超时时，如果有排队的最新图片，启动新传输
            if (pending != null)
            {
                StartImageTransfer(pending);
            }
        }

        // /t 发送后等待 /k：主动重试 /t，若仍未收到 /k 则直接开始发图
        private async Task WaitForKWithRetryAsync(ulong transferId, CancellationToken ct, int maxRetryCount, int retryIntervalMs)
        {
            for (int attempt = 0; attempt < maxRetryCount; attempt++)
            {
                try
                {
                    await Task.Delay(retryIntervalMs, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                lock (_imageTransferLock)
                {
                    if (_transferId != transferId || _imagePhase != ImageTransferPhase.Starting)
                        return;
                }

                ImageTransferStatusText.Text = $"未收到 /k，第 {attempt + 1} 次重试 /t...";
                _serialPortService.SendImageStartCommand();
            }

            // 最后一次重试后再等 200ms，若仍未收到 /k 则直接发图
            try
            {
                await Task.Delay(200, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            byte[]? currentImage = null;
            lock (_imageTransferLock)
            {
                if (_transferId != transferId || _imagePhase != ImageTransferPhase.Starting)
                    return;

                currentImage = _pendingImageData ?? _imageDataBeingSent;
                _pendingImageData = null;
                _imagePhase = ImageTransferPhase.Transferring;
            }

            if (currentImage != null)
            {
                ImageTransferStatusText.Text = "多次重试 /t 仍未收到 /k，直接开始发送图片...";
                _ = SendImageAsync(transferId, currentImage, CancellationToken.None);
            }
        }

        // 等待串口线路安静：自上次接收数据以来至少 quietPeriod 没有新数据，或达到 maxWait 超时
        private async Task<bool> WaitForLineQuietAsync(TimeSpan quietPeriod, TimeSpan maxWait, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < maxWait)
            {
                ct.ThrowIfCancellationRequested();
                DateTime lastReceive;
                lock (_receiveTimeLock) lastReceive = _lastSerialReceiveTime;
                if (lastReceive != DateTime.MinValue && DateTime.Now - lastReceive >= quietPeriod)
                {
                    return true;
                }
                await Task.Delay(20, ct);
            }
            return true; // 超时仍继续，不阻塞同步
        }

        // 处理重传单个包请求
        private void HandleResendSinglePacket(ushort packetIndex)
        {
            byte[]? imageData;

            lock (_imageTransferLock)
            {
                imageData = _imageDataBeingSent;
                if (_imagePhase != ImageTransferPhase.Transferring &&
                    _imagePhase != ImageTransferPhase.AwaitingAck)
                {
                    return;
                }
            }

            if (imageData == null) return;

            int totalPackets = ImageProcessor.CalculatePacketCount(imageData);
            if (packetIndex >= totalPackets) return;

            _serialPortService.SendImagePacket(packetIndex, imageData);
            _serialPortService.SendImageEndCommand();
        }

        // 处理重传后续包请求
        private void HandleResendFromPacket(ushort startPacketIndex)
        {
            byte[]? imageData;

            lock (_imageTransferLock)
            {
                imageData = _imageDataBeingSent;
                if (_imagePhase != ImageTransferPhase.Transferring &&
                    _imagePhase != ImageTransferPhase.AwaitingAck)
                {
                    return;
                }
            }

            if (imageData == null) return;

            int totalPackets = ImageProcessor.CalculatePacketCount(imageData);
            if (startPacketIndex >= totalPackets) return;

            _ = ResendFromAsync(startPacketIndex, imageData);
        }

        private async Task ResendFromAsync(int startPacketIndex, byte[] imageData)
        {
            try
            {
                int totalPackets = ImageProcessor.CalculatePacketCount(imageData);

                for (int i = startPacketIndex; i < totalPackets; i++)
                {
                    lock (_imageTransferLock)
                    {
                        if (_imagePhase != ImageTransferPhase.Transferring &&
                            _imagePhase != ImageTransferPhase.AwaitingAck)
                        {
                            return;
                        }
                    }

                    _serialPortService.SendImagePacket(i, imageData);

                    if (i % 64 == 0)
                    {
                        await Task.Yield();
                    }
                }

                _serialPortService.SendImageEndCommand();
            }
            catch (Exception ex)
            {
                ImageTransferStatusText.Text = $"重传后续包失败: {ex.Message}";
            }
        }

        // 图片包发送事件处理
        private void OnImagePacketSent(object sender, SerialPortService.ImagePacketEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (e.Success)
                {
                    ImageTransferStatusText.Text =
                        $"发送进度: {e.PacketIndex + 1}/{e.TotalPackets} 包 " +
                        $"(字节 {e.DataStartIndex}-{e.DataStartIndex + e.DataLength})";
                }
                else
                {
                    ImageTransferStatusText.Text = $"包 {e.PacketIndex} 发送失败: {e.ErrorMessage}";
                }
            });
        }

        // 自动连接设备
        private void AutoConnectDevice()
        {
            bool success = _serialPortService.AutoConnect();

            if (!success)
            {
                // 连接失败，启用自动重连定时器
                _autoReconnectTimer.IsEnabled = true;
            }
        }

        // 播放状态定时发送事件
        private void StatusSendTimer_Tick(object sender, EventArgs e)
        {
            if (!_serialPortService.IsConnected) return;

            // 图片下落动画期间抑制状态命令，避免打断动画
            if (DateTime.Now < _suppressStatusUntil) return;

            // 图片传输过程中暂停发送状态命令
            lock (_imageTransferLock)
            {
                if (_imagePhase != ImageTransferPhase.Idle && _imagePhase != ImageTransferPhase.Completed)
                    return;
            }

            // 根据当前播放状态发送相应命令，发送失败则触发重连
            if (_currentPlayStatus == "Playing")
            {
                if (!_serialPortService.SendPlaybackStatus(true))
                {
                    AutoConnectDevice();
                }
            }
            else if (_currentPlayStatus == "Paused")
            {
                if (!_serialPortService.SendPlaybackStatus(false))
                {
                    AutoConnectDevice();
                }
            }
        }

        // 自动重连定时器事件
        private void AutoReconnectTimer_Tick(object sender, EventArgs e)
        {
            if (!_serialPortService.IsConnected)
            {
                bool success = _serialPortService.AutoConnect();

                if (success)
                {
                    _autoReconnectTimer.IsEnabled = false;
                }
            }
            else
            {
                _autoReconnectTimer.IsEnabled = false;
            }
        }



        // ==================== 控件回调 ====================

        // 播放按钮点击事件
        private async void SysPlayButton_Click(object sender, RoutedEventArgs e)
        {
            bool success = await _mediaService.TryPlayAsync();

            //ControlResultText.Text = success ? "播放命令已发送" : "播放命令发送失败";
            //ControlResultText.Foreground = success ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Red;
        }

        // 暂停按钮点击事件
        private async void SysPauseButton_Click(object sender, RoutedEventArgs e)
        {
            bool success = await _mediaService.TryPauseAsync();

            //ControlResultText.Text = success ? "暂停命令已发送" : "暂停命令发送失败";
            //ControlResultText.Foreground = success ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Red;
        }

        // 开机自启动复选框状态改变
        private void AutoStartCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_autoStartManager == null) return;

            bool isChecked = AutoStartCheckBox.IsChecked ?? false;

            try
            {
                if (isChecked)
                {
                    // 启用开机自启动
                    bool success = _autoStartManager.EnableAutoStart();

                    if (success)
                    {
                        // 显示成功提示
                        //System.Windows.MessageBox.Show("开机自启动已启用。\n\n启动文件夹: " +
                        //    _autoStartManager.GetStartupFolderDisplayPath(),
                        //    "设置成功",
                        //    MessageBoxButton.OK,
                        //    MessageBoxImage.Information);
                    }
                }
                else
                {
                    // 禁用开机自启动
                    bool success = _autoStartManager.DisableAutoStart();

                    if (success)
                    {
                        // 成功禁用开机自启动
                    }
                }
            }
            catch (Exception ex)
            {
                // 恢复复选框状态
                AutoStartCheckBox.IsChecked = !isChecked;

                // 显示错误信息
                string errorMessage = $"设置失败: {ex.Message}";

                System.Windows.MessageBox.Show($"设置开机自启动失败:\n\n{ex.Message}",
                    "设置失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // 连接按钮点击事件
        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_serialPortService.IsConnected)
            {
                // 如果已连接，则断开
                _serialPortService.Disconnect();
            }
            else
            {
                // 否则尝试连接
                AutoConnectDevice();
            }
        }

        // 断开连接按钮点击事件
        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            _serialPortService.Disconnect();
        }



        // ==================== 窗口事件处理 ====================

        // 窗口关闭事件
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (App.IsSecondInstance)
            {
                // 这是第二个实例，直接关闭，不需要隐藏到托盘
                //e.Cancel = false; // 允许关闭
                return;
            }

            // 如果是从托盘菜单退出，则正常关闭
            if (_isExitingFromTrayMenu)
            {
                return;
            }

            // 否则取消关闭操作，最小化到托盘
            e.Cancel = true;
            HideWindowToTray();
        }

        // 窗口状态改变事件
        private void Window_StateChanged(object sender, EventArgs e)
        {
            // 当窗口最小化时，隐藏到托盘
            if (this.WindowState == WindowState.Minimized && !_isExitingFromTrayMenu)
            {
                HideWindowToTray();
            }
        }

    }
}
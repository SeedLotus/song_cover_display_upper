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
        private DispatcherTimer _statusSendTimer = null!;     // 定时发送播放状态到串口（InitializeTimers 中赋值）
        private DispatcherTimer _autoReconnectTimer = null!;  // 自动重连定时器（InitializeTimers 中赋值）

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
        private DateTime _suppressStatusUntil = DateTime.MinValue; // 抑制状态 topic 发送的时间点

        // 下位机唱臂状态跟踪：下位机把 /1 和 /0 都当作"切换状态"的 topic，
        // 因此上位机必须自己维护下位机 believed state，只在需要切换时发送 topic。
        private bool _lowerMachinePlaying = false;

        private bool _autoRetryOnAckTimeout = false;  // 重连后首次封面同步 /a 超时是否自动重试一次
        private DateTime _transferStartTime;           // 本次传输开始时间（用于耗时日志）

        // 专辑封面处理串行锁：防止 SMTC 快速重复事件导致同一封面被并发处理两次
        private readonly SemaphoreSlim _processAlbumArtLock = new SemaphoreSlim(1, 1);

        // 媒体事件防抖：SMTC 事件触发瞬间属性可能仍是上一个媒体的，需延迟后主动重取
        private CancellationTokenSource? _mediaDebounceCts;
        // 两段式拉取：120ms 首次拉取（尽快出图），500ms 确认拉取（兜住 SMTC 滞后更新）
        private static readonly TimeSpan MediaFirstFetchDelay = TimeSpan.FromMilliseconds(120);
        private static readonly TimeSpan MediaConfirmFetchDelay = TimeSpan.FromMilliseconds(500);

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
                System.Diagnostics.Debug.WriteLine($"媒体服务初始化失败: {ex.Message}");
            }
        }

        // 初始化托盘服务
        private void InitializeTrayService()
        {
            string? iconPath = GetEmbeddedIconAsTempFile();
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
        public string? GetEmbeddedIconAsTempFile()
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
                System.Diagnostics.Debug.WriteLine($"开机自启动状态检测失败: {ex.Message}");
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
        private async void OnMediaInfoChanged(object? sender, MediaService.MediaInfoChangedEventArgs e)
        {
            // 立即更新文本显示，保证响应速度（数据可能滞后，防抖后会用新鲜数据修正）
            await Dispatcher.InvokeAsync(() =>
            {
                TitleTextBlock.Text = e.Title;
                ArtistTextBlock.Text = e.Artist;
                AlbumTextBlock.Text = e.Album;
            });

            // SMTC 的 MediaPropertiesChanged 触发瞬间，会话属性可能尚未更新——
            // 此时读到的标题/缩略图还是上一个媒体的。若直接使用事件数据，
            // 会把新标题与旧封面绑定，后续真正的封面事件又被去重吞掉，
            // 表现为"上位机显示上一个视频的封面且不再更新"（2026-09-23 实机复现）。
            // 对策：两段式主动拉取——120ms 后首拉（尽快出图），500ms 后确认拉取
            // （兜住滞后的缩略图更新；下游 hash 判重保证确认阶段不会重复发图）。
            _mediaDebounceCts?.Cancel();
            _mediaDebounceCts = new CancellationTokenSource();
            var token = _mediaDebounceCts.Token;

            _ = ProcessMediaPipelineWithConfirmationAsync(token);
        }

        // 两段式媒体处理流水线：首拉求快，确认拉取求准
        private async Task ProcessMediaPipelineWithConfirmationAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(MediaFirstFetchDelay, token);
            }
            catch (OperationCanceledException)
            {
                return; // 120ms 内又来了新事件，由新事件接管
            }

            var fresh = await _mediaService.GetCurrentMediaInfoAsync();
            if (fresh == null || token.IsCancellationRequested) return;

            await ProcessFreshMediaInfoAsync(fresh, forceHashCheck: false);

            // 确认阶段：SMTC 缩略图可能滞后于标题更新，500ms 时再拉一次校验。
            // 若缩略图已更新为真正的新图，hash 会不同并触发重处理；若无变化，hash 相同直接跳过。
            try
            {
                await Task.Delay(MediaConfirmFetchDelay - MediaFirstFetchDelay, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var confirm = await _mediaService.GetCurrentMediaInfoAsync();
            if (confirm == null || token.IsCancellationRequested) return;

            await ProcessFreshMediaInfoAsync(confirm, forceHashCheck: true);
        }

        // 用一份新鲜的媒体数据刷新 UI 并进入封面处理流水线
        // forceHashCheck=true 时跳过身份捷径，强制做缩略图 hash 比对（用于确认阶段）
        private async Task ProcessFreshMediaInfoAsync(MediaService.MediaInfoChangedEventArgs info, bool forceHashCheck)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                TitleTextBlock.Text = info.Title;
                ArtistTextBlock.Text = info.Artist;
                AlbumTextBlock.Text = info.Album;
            });

            bool identityChanged = info.Title != _lastMediaTitle
                                || info.Artist != _lastMediaArtist
                                || info.Album != _lastMediaAlbum;

            System.Diagnostics.Debug.WriteLine(
                $"[Media] 拉取数据: {info.Title} / {info.Artist} (上次: {_lastMediaTitle}, 身份变化: {identityChanged}, 强制hash: {forceHashCheck})");

            if (info.ThumbnailStream is Windows.Storage.Streams.IRandomAccessStreamReference thumbnailRef)
            {
                if (!forceHashCheck && !identityChanged && _currentImageRgb565Data != null)
                {
                    // 同一媒体且已处理过封面，跳过（播放/暂停触发的重复事件走这里，无需解码）
                    return;
                }

                // 注意：_lastMediaTitle/_lastImageHash 不在此处更新，
                // 而是放到 ProcessAlbumArtAsync 中，在确认缩略图 hash 真正变化后再更新。
                // 这样可以避免 SMTC 缩略图滞后（标题已变但缩略图还是旧图）导致的错位。
                var title = info.Title;
                var artist = info.Artist;
                var album = info.Album;

                // 图片处理涉及大量 DispatcherObject，统一放到 UI 线程执行
                await Dispatcher.InvokeAsync(async () => await ProcessAlbumArtAsync(thumbnailRef, title, artist, album));
            }
            else
            {
                // 无缩略图时（例如切换中的临时状态），不更新 _lastMediaTitle/_lastImageHash，
                // 避免后续真正带缩略图的事件因 identityChanged=false 被跳过，
                // 也避免清空 hash 后把滞后的旧缩略图误判为新图。
                await Dispatcher.InvokeAsync(() =>
                {
                    // 显示默认占位图
                    AlbumArtImage.Source = ImageProcessor.CreatePlaceholderImage();
                    // 注意：不在这里清空 _currentImageRgb565Data/_lastImageHash，
                    // 否则下位机会继续显示上一张封面；如需发送占位图应单独处理。
                });
            }
        }

        // 播放状态变化事件处理
        private void OnPlaybackStateChanged(object? sender, MediaService.PlaybackStateChangedEventArgs e)
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

                // PC 播放状态变化时，与下位机 believed state 对比，只在需要切换时发送 topic。
                // 下位机把 /1 和 /0 都当作切换命令，重复发送会导致摇臂反向运动。
                SyncPlaybackStatusToLowerMachine();
            });
        }

        // 媒体会话可用性变化事件处理
        private void OnSessionAvailabilityChanged(object? sender, bool isAvailable)
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

        // 根据 PC 当前播放状态与下位机 believed state 的对比，决定是否需要发送 topic 切换。
        // 下位机收到 /1 或 /0 都会切换状态，因此必须避免重复发送。
        private void SyncPlaybackStatusToLowerMachine()
        {
            if (!_serialPortService.IsConnected) return;

            bool pcPlaying = _currentPlayStatus == "Playing";
            if (pcPlaying == _lowerMachinePlaying) return;

            // 发送 /1 或 /0 效果相同：都是让下位机切换状态的 topic
            if (_serialPortService.SendPlaybackStatus(pcPlaying))
            {
                _lowerMachinePlaying = pcPlaying;
            }
        }

        // ==================== 图片处理逻辑 ====================

        // 处理并显示专辑封面
        private async Task ProcessAlbumArtAsync(
            Windows.Storage.Streams.IRandomAccessStreamReference thumbnailRef,
            string title, string artist, string album)
        {
            // 串行化封面处理，避免 SMTC 快速重复事件并发进入导致同一封面被发送两次
            await _processAlbumArtLock.WaitAsync();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // 跨会话切换后旧会话的缩略图流可能永远读不出来；
                // 加 3 秒超时，防止 _processAlbumArtLock 被永久占用导致整个封面流水线锁死
                var openTask = thumbnailRef.OpenReadAsync().AsTask();
                if (await Task.WhenAny(openTask, Task.Delay(3000)) != openTask)
                {
                    System.Diagnostics.Debug.WriteLine($"[Media] 缩略图读取超时(3s)，跳过: {title}");
                    await Dispatcher.InvokeAsync(() =>
                    {
                        ImageTransferStatusText.Text = "缩略图读取超时，跳过本次封面处理";
                    });
                    return;
                }

                // 加载原始图像
                using (var stream = openTask.Result)
                {
                    var originalBitmap = new System.Windows.Media.Imaging.BitmapImage();
                    originalBitmap.BeginInit();
                    originalBitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    originalBitmap.StreamSource = stream.AsStream();
                    originalBitmap.EndInit();
                    originalBitmap.Freeze();

                    // 图片 hash 是唯一去重判据，同时覆盖两种场景：
                    // 1. SMTC 缩略图滞后（标题已变但缩略图还是旧图）→ hash 相同，跳过且不烧身份；
                    // 2. 同一身份下缩略图滞后更新（确认阶段强制进入）→ hash 不同，正常处理。
                    // 因此不再使用"同身份直接跳过"的捷径——它会把确认阶段的合法更新挡在门外。
                    string newHash = ImageProcessor.ComputeImageHash(originalBitmap);
                    if (newHash == _lastImageHash)
                    {
                        // 图片 hash 未变化，说明是同一封面，直接返回。
                        return;
                    }

                    // 真正的新封面：立即更新媒体身份与 hash，这样后续重复/滞后事件能正确判断。
                    _lastMediaTitle = title;
                    _lastMediaArtist = artist;
                    _lastMediaAlbum = album;
                    _lastImageHash = newHash;

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
                    var rgb565Data = await Task.Run(() =>
                    {
                        if (processedImage == null) return null;
                        return ImageProcessor.ConvertToRgb565(processedImage);
                    });

                    if (rgb565Data == null)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            ImageTransferStatusText.Text = "封面编码失败";
                        });
                        return;
                    }

                    // 注意：此处不再做"编码后身份复核"。_processAlbumArtLock 已串行化整个处理流程，
                    // 且 _lastMediaTitle 等字段仅由本函数在持锁期间更新，复核条件恒不成立（原为死代码）。
                    // 防封面错位的真实机制：semaphore 串行化 + 缩略图 hash 检查 + StartImageTransfer 的 pending 替换。

                    _currentImageRgb565Data = rgb565Data;

                    // 更新状态显示
                    await Dispatcher.InvokeAsync(() =>
                    {
                        int totalPackets = ImageProcessor.CalculatePacketCount(rgb565Data);
                        ImageTransferStatusText.Text =
                            $"✅ 封面图片就绪 ({rgb565Data.Length} 字节, {totalPackets} 包)";
                    });

                    // 启动图片传输状态机
                    StartImageTransfer(rgb565Data);

                    System.Diagnostics.Debug.WriteLine(
                        $"[Media] 封面处理完成，耗时 {sw.ElapsedMilliseconds}ms: {title}");
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
            finally
            {
                _processAlbumArtLock.Release();
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
                System.Diagnostics.Debug.WriteLine($"默认浏览器打开失败，尝试备用方式: {ex.Message}");
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
        private async void OnSerialConnectionChanged(object? sender, bool isConnected)
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
                        _lowerMachinePlaying = false; // /0 将下位机复位到暂停态
                        await Task.Delay(WakeCommandDelay);

                        if (!_serialPortService.IsConnected)
                        {
                            ImageTransferStatusText.Text = "同步前连接已断开，取消本次同步";
                            return;
                        }

                        // /0 会让摇臂进入暂停态；若 PC 正在播放，立即补发 /1 让摇臂到位，
                        // 避免封面同步完成前摇臂长时间放下。
                        ImageTransferStatusText.Text = "同步摇臂位置到当前播放状态...";
                        SyncPlaybackStatusToLowerMachine();
                        await Task.Delay(300);

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
                        _autoRetryOnAckTimeout = true;
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
        private void OnSerialStatusMessage(object? sender, string message)
        {
            // 将串口状态消息显示在 UI 上，便于现场诊断
            Dispatcher.InvokeAsync(() =>
            {
                ImageTransferStatusText.Text = message;
            });
        }

        // 串口命令接收事件处理
        // 用 InvokeAsync 而非 Invoke：避免 UI 繁忙时同步阻塞串口接收线程造成反压
        private void OnSerialCommandReceived(object? sender, SerialPortService.SerialCommandReceivedEventArgs e)
        {
            Dispatcher.InvokeAsync(() => ProcessSerialCommand(e));
        }

        // 处理串口接收到的结构化命令
        private void ProcessSerialCommand(SerialPortService.SerialCommandReceivedEventArgs e)
        {
            switch (e.CommandText)
            {
                case "/q1":
                    // 下位机请求播放命令
                    ImageTransferStatusText.Text = "收到 /q1，请求播放";
                    _lowerMachinePlaying = false; // 下位机当前认为自己在暂停态
                    if (_currentPlayStatus == "Playing")
                    {
                        // PC 已经在播放，直接同步一次 topic 让下位机切换
                        SyncPlaybackStatusToLowerMachine();
                    }
                    else
                    {
                        // 请求 PC 播放，待 SMTC 事件触发后由 OnPlaybackStateChanged 发送 topic
                        _ = _mediaService.TryPlayAsync();
                    }
                    break;

                case "/q0":
                    // 下位机请求暂停命令
                    ImageTransferStatusText.Text = "收到 /q0，请求暂停";
                    _lowerMachinePlaying = true; // 下位机当前认为自己在播放态
                    if (_currentPlayStatus == "Paused")
                    {
                        SyncPlaybackStatusToLowerMachine();
                    }
                    else
                    {
                        _ = _mediaService.TryPauseAsync();
                    }
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
                // 如果当前还在等 /k（Starting），下位机尚未开始收图包，
                // 可以直接替换为新图，避免快速切换时 pending 丢失或状态卡住。
                if (_imagePhase == ImageTransferPhase.Starting)
                {
                    _imageTransferCts?.Cancel();
                    _transferId++;
                    currentTransferId = _transferId;
                    _imageDataBeingSent = imageData;
                    _pendingImageData = null;
                    _imageTransferCts = new CancellationTokenSource();
                    // _imagePhase 保持 Starting
                }
                // 如果正在传输中或等待 /a，先排队为 pending 并请求取消当前传输，
                // 等当前传输自然结束（收到 /a 或超时）后再启动新传输，
                // 避免向下位机发送重叠的 /t 命令。
                else if (_imagePhase != ImageTransferPhase.Idle)
                {
                    _pendingImageData = imageData;
                    _imageTransferCts?.Cancel();
                    return;
                }
                else
                {
                    _transferId++;
                    currentTransferId = _transferId;
                    _imageDataBeingSent = imageData;
                    _pendingImageData = null;
                    _imagePhase = ImageTransferPhase.Starting;
                    _imageTransferCts = new CancellationTokenSource();
                }
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
            _transferStartTime = DateTime.Now;
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

                // 批量 16 包、每批一次 Flush（原 8 包/Flush 刷新次数过多，实测延迟偏高；
                // 若下位机出现丢包，/r /x 重传机制可自愈）
                const int BATCH_SIZE = 16;
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

                // 1000ms 内未收到 /a 则超时；不再重试 /o，避免下位机重复播放下落动画。
                _ = WaitForPhaseTimeoutAsync(transferId, ImageTransferPhase.AwaitingAck, 1000);
            }
            catch (OperationCanceledException)
            {
                // 传输被取消（通常是更新的封面在排队）。取消时状态机仍停留在 Transferring，
                // 必须在此复位并启动 pending，否则阶段永久卡死、后续封面全部无法下发
                // （2026-09-23 实机复现：PotPlayer↔B站 跨会话切换后锁死在旧封面）。
                // 下位机收到下一次 /t 会自行丢弃残包并重置接收状态，直接放弃当前传输是安全的。
                byte[]? pending = null;
                lock (_imageTransferLock)
                {
                    if (_transferId == transferId && _imagePhase == ImageTransferPhase.Transferring)
                    {
                        _imagePhase = ImageTransferPhase.Idle;
                        pending = _pendingImageData;
                        _pendingImageData = null;
                    }
                }

                if (pending != null)
                {
                    // 稍等片刻再启动新图，给下位机状态机留出过渡时间
                    _ = Task.Delay(300).ContinueWith(_ =>
                    {
                        Dispatcher.Invoke(() => StartImageTransfer(pending));
                    });
                }
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
                _autoRetryOnAckTimeout = false;
            }

            ImageTransferStatusText.Text = "✅ 收到 /a，图片发送完成";
            System.Diagnostics.Debug.WriteLine(
                $"[Transfer] /t→/a 耗时 {(DateTime.Now - _transferStartTime).TotalMilliseconds:F0}ms");

            // 图片到位后同步当前播放状态，确保摇臂位置与 PC 一致，
            // 但只在 PC 状态与下位机 believed state 不一致时才发送 topic，避免误切换。
            SyncPlaybackStatusToLowerMachine();

            if (pending != null)
            {
                // 稍等片刻再启动下一张图，让下位机完成图片下落动画和状态机过渡
                _ = Task.Delay(200).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() => StartImageTransfer(pending));
                });
            }
            else
            {
                lock (_imageTransferLock)
                {
                    _imagePhase = ImageTransferPhase.Idle;
                }
            }
        }

        // 传输阶段超时处理
        private async Task WaitForPhaseTimeoutAsync(ulong transferId, ImageTransferPhase expectedPhase, int milliseconds)
        {
            try
            {
                await Task.Delay(milliseconds, _imageTransferCts?.Token ?? default);
            }
            catch (OperationCanceledException)
            {
                // 取消不代表可以撒手不管：取消只意味着有更新的图在排队。
                // 若此刻仍处于原阶段（例如 /a 永远不会来的 AwaitingAck），
                // 必须继续走下面的超时收尾逻辑，否则状态机永久卡死。
                // 注意：收到 /a 的正常路径里 HandleImageTransferAck 也会取消 CTS，
                // 但那时阶段已变为 Completed，下面的阶段检查会正确放行。
            }

            lock (_imageTransferLock)
            {
                if (_transferId != transferId || _imagePhase != expectedPhase)
                    return;
            }

            // 注意：不再重试 /o。下位机每次收到 /o 都会播放一次下落动画，
            // 重试 /o 会导致同一封面被播放两次。若 /a 超时，依赖后续切换视频重新传输，
            // 或重连后的 autoRetry 机制。

            byte[]? pending = null;
            bool autoRetry = false;
            byte[]? currentImage = null;

            lock (_imageTransferLock)
            {
                if (_transferId != transferId || _imagePhase != expectedPhase)
                    return;

                _imagePhase = ImageTransferPhase.Idle;
                ImageTransferStatusText.Text = $"图片传输超时: {expectedPhase}";
                pending = _pendingImageData;
                _pendingImageData = null;
                autoRetry = _autoRetryOnAckTimeout;
                _autoRetryOnAckTimeout = false;
                currentImage = _currentImageRgb565Data;
            }

            // 重连后首次封面同步 /a 超时，自动重试一次完整传输
            if (autoRetry && currentImage != null)
            {
                ImageTransferStatusText.Text = "重连后首次同步未收到 /a，自动重试一次...";
                _ = Task.Delay(500).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() => StartImageTransfer(currentImage, kRetryCount: ReconnectKRetryCount, kRetryIntervalMs: ReconnectKRetryIntervalMs));
                });
            }
            // 其他阶段超时时，如果有排队的最新图片，启动新传输
            else if (pending != null)
            {
                _ = Task.Delay(200).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() => StartImageTransfer(pending));
                });
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
        // 一张封面 1888 个包，每包都 Dispatcher 调度会把 UI 线程淹掉；
        // 失败立即上报，成功进度每 128 包或最后一包才刷新一次。
        private int _lastPacketProgressReported = -1;

        private void OnImagePacketSent(object? sender, SerialPortService.ImagePacketEventArgs e)
        {
            if (e.Success)
            {
                bool isLast = e.PacketIndex + 1 >= e.TotalPackets;
                if (!isLast && e.PacketIndex - _lastPacketProgressReported < 128)
                {
                    return;
                }
                _lastPacketProgressReported = e.PacketIndex;
            }
            else
            {
                // 失败后重置进度采样，让下一批传输从头开始上报
                _lastPacketProgressReported = -1;
            }

            Dispatcher.InvokeAsync(() =>
            {
                if (e.Success)
                {
                    ImageTransferStatusText.Text =
                        $"发送进度: {e.PacketIndex + 1}/{e.TotalPackets} 包";
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
        private void StatusSendTimer_Tick(object? sender, EventArgs e)
        {
            if (!_serialPortService.IsConnected) return;

            // 图片下落动画期间抑制状态 topic，避免打断动画
            if (DateTime.Now < _suppressStatusUntil) return;

            // 图片传输过程中暂停发送状态 topic
            lock (_imageTransferLock)
            {
                if (_imagePhase != ImageTransferPhase.Idle && _imagePhase != ImageTransferPhase.Completed)
                    return;
            }

            // 与下位机 believed state 对比，只在不一致时补发 topic，
            // 避免下位机把周期心跳当成切换指令。
            bool pcPlaying = _currentPlayStatus == "Playing";
            if (pcPlaying != _lowerMachinePlaying)
            {
                SyncPlaybackStatusToLowerMachine();
            }
            else
            {
                // 状态一致时改发 /h 心跳（仅刷新下位机休眠计时，不碰播放状态机），
                // 防止下位机 60 秒无命令进入休眠（DEV_LOG 3.6）。需配套新版固件。
                _serialPortService.SendHeartbeat();
            }
        }

        // 自动重连定时器事件
        private void AutoReconnectTimer_Tick(object? sender, EventArgs e)
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

        /// <summary>
        /// 进程退出前统一释放各服务资源。
        /// 不清理会导致进程退出后托盘图标残留（鼠标划过才消失）、串口/媒体会话句柄悬挂。
        /// 由 App.OnExit 调用；关闭到托盘不会触发。
        /// </summary>
        public void CleanupServices()
        {
            try { _statusSendTimer?.Stop(); } catch { }
            try { _autoReconnectTimer?.Stop(); } catch { }
            try { _imageTransferCts?.Cancel(); } catch { }
            try { _mediaService?.Dispose(); } catch { }
            try { _serialPortService?.Dispose(); } catch { }
            try { _trayService?.Dispose(); } catch { }
        }

    }
}
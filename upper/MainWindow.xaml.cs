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
        private bool _isExitingFromTrayMenu = false;  // 是否从托盘菜单退出
        private DispatcherTimer _statusSendTimer;     // 定时发送播放状态到串口
        private DispatcherTimer _autoReconnectTimer;  // 自动重连定时器


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
                this.Close();
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
            _serialPortService.DataReceived += OnSerialDataReceived;
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

            // 处理专辑封面（如果有）
            if (e.ThumbnailStream is Windows.Storage.Streams.IRandomAccessStreamReference thumbnailRef)
            {
                await ProcessAlbumArtAsync(thumbnailRef);
            }
            else
            {
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
                // 播放中时禁用播放按钮，启用暂停按钮
                // 暂停/停止时启用播放按钮，禁用暂停按钮
                bool isPlaying = e.State == "Playing";
                SysPlayButton.IsEnabled = !isPlaying;
                SysPauseButton.IsEnabled = isPlaying;

                //ControlResultText.Text = isPlaying ? "正在播放" : "已暂停";
                //ControlResultText.Foreground = isPlaying ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Orange;

                // 发送播放状态给下位机
                //_serialPortService.SendPlaybackStatus(isPlaying);
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

                    // 处理图片（智能缩放 + 模糊背景）
                    var processedImage = ImageProcessor.ProcessAlbumArt(originalBitmap);

                    // 更新UI显示
                    await Dispatcher.InvokeAsync(() =>
                    {
                        AlbumArtImage.Source = processedImage;
                    });

                    // 编码为RGB565（用于发送到下位机）
                    _currentImageRgb565Data = ImageProcessor.ConvertToRgb565(processedImage);
                    _lastImageHash = newHash;

                    // 更新状态显示
                    await Dispatcher.InvokeAsync(() =>
                    {
                        int totalPackets = ImageProcessor.CalculatePacketCount(_currentImageRgb565Data);
                        ImageTransferStatusText.Text =
                            $"✅ 封面图片就绪 ({_currentImageRgb565Data.Length} 字节, {totalPackets} 包)";
                        //SendImageButton.IsEnabled = _serialPortService.IsConnected;
                    });
                    // 通知下位机准备接收图片
                    _serialPortService.SendImageStartCommand();
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    AlbumArtImage.Source = ImageProcessor.CreatePlaceholderImage();
                    _currentImageRgb565Data = null;
                    _lastImageHash = null;
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
        private void OnSerialConnectionChanged(object sender, bool isConnected)
        {
            Dispatcher.Invoke(() =>
            {
                // 更新连接状态指示灯
                StatusLed.Fill = isConnected ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Red;
                ConnectionStatusText.Text = isConnected
                    ? $"已连接到 {_serialPortService.ConnectedPort}"
                    : "设备未连接";

                // 更新按钮状态
                //ConnectButton.Content = isConnected ? "重新连接" : "连接设备";
                ConnectButton.IsEnabled = !isConnected;
                DisconnectButton.IsEnabled = isConnected;
                //SendImageButton.IsEnabled = isConnected && (_currentImageRgb565Data != null);

                // 启动或停止状态发送定时器
                if (isConnected)
                {
                    _statusSendTimer.Start();
                    _autoReconnectTimer.IsEnabled = false; // 连接成功时关闭自动重连
                }
                else
                {
                    _statusSendTimer.Stop();
                }
            });
        }

        // 串口状态消息事件处理
        private void OnSerialStatusMessage(object sender, string message)
        {
            return;
            Dispatcher.Invoke(() =>
            {
            });
        }

        // 串口数据接收事件处理
        private void OnSerialDataReceived(object sender, SerialPortService.DataReceivedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                // 处理控制命令
                ProcessSerialCommand(e.AsciiString);
            });
        }

        // 处理串口接收到的命令
        private void ProcessSerialCommand(string command)
        {
            // 移除可能的空白字符
            //string trimmedCommand = command.Trim();
            // 不移除
            string trimmedCommand = command;

            if (trimmedCommand.StartsWith("/q1"))
            {
                // 下位机请求播放命令
                // 返回系统音频已播放命令
                _serialPortService.SendPlaybackStatus(true);
                // 播放系统音频
                _ = _mediaService.TryPlayAsync();
            }
            else if (trimmedCommand.StartsWith("/q0"))
            {
                // 下位机请求暂停命令
                // 返回系统音频已暂停命令
                _serialPortService.SendPlaybackStatus(false);
                // 暂停系统音频
                _ = _mediaService.TryPauseAsync();
            }
            else if (trimmedCommand.StartsWith("/r"))
            {
                // 重传单个包请求
                HandleResendSinglePacket(trimmedCommand);
            }
            else if (trimmedCommand.StartsWith("/x"))
            {
                // 重传后续包请求
                HandleResendFromPacket(trimmedCommand);
            }
            else if (trimmedCommand.StartsWith("/k"))
            {
                // 下位机准备接收图片就绪响应
                // 发送全图
                _SendImage();
            }
            else if (trimmedCommand.StartsWith("/a"))
            {
                // 下位机成功接收所有包
            }
        }

        // 处理重传单个包请求，todo
        private void HandleResendSinglePacket(string command)
        {
            try
            {
                // 解析包序号
                if (false)
                {
                    // 收到重传单个包请求 packetIndex

                    // 执行重传
                    if (_currentImageRgb565Data != null)
                    {
                        //_serialPortService.SendImagePacket(packetIndex, _currentImageRgb565Data);
                        //_serialPortService.SendImageEndCommand();
                    }
                }
            }
            catch (Exception ex)
            {
                // 处理重传请求失败
            }
        }

        // 处理重传后续包请求，todo
        private void HandleResendFromPacket(string command)
        {
            try
            {
                // 解析起始包序号
                if (false)
                {
                    // 收到重传后续包请求: 从包 startPacketIndex 开始

                    // 执行重传
                    if (_currentImageRgb565Data != null)
                    {
                        int totalPackets = ImageProcessor.CalculatePacketCount(_currentImageRgb565Data);

                        //for (int i = startPacketIndex; i < totalPackets; i++)
                        //{
                        //    _serialPortService.SendImagePacket(i, _currentImageRgb565Data);
                        //}

                        //_serialPortService.SendImageEndCommand();
                    }
                }
            }
            catch (Exception ex)
            {
                // 处理重传请求失败
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

        // 发送图片服务
        private async void _SendImage()
        {
            if (!_serialPortService.IsConnected)
            {
                System.Windows.MessageBox.Show("请先连接设备", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (_currentImageRgb565Data == null)
            {
                System.Windows.MessageBox.Show("没有可发送的图片数据", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                //SendImageButton.IsEnabled = false;
                int totalPackets = ImageProcessor.CalculatePacketCount(_currentImageRgb565Data);
                ImageTransferStatusText.Text = $"开始传输图片，共 {totalPackets} 包...";

                // 1. 发送开始传输命令
                //_serialPortService.SendImageStartCommand();
                //await Task.Delay(100); // 等待下位机响应

                // 2. 发送所有数据包
                for (int i = 0; i < totalPackets; i++)
                {
                    _serialPortService.SendImagePacket(i, _currentImageRgb565Data);
                }
                await Task.Delay(1); // 微小延迟，避免覆盖

                // 3. 发送结束命令
                _serialPortService.SendImageEndCommand();

                ImageTransferStatusText.Text = $"✅ 图片发送完成，共 {totalPackets} 包";
            }
            catch (Exception ex)
            {
                ImageTransferStatusText.Text = $"发送失败: {ex.Message}";
            }
            finally
            {
                //SendImageButton.IsEnabled = true;
            }
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

            // 根据当前播放状态发送相应命令
            if (_currentPlayStatus == "Playing")
            {
                _serialPortService.SendPlaybackStatus(true);
            }
            else if (_currentPlayStatus == "Paused")
            {
                _serialPortService.SendPlaybackStatus(false);
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
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
        private bool _isExitingFromTrayMenu = false;  // 是否从托盘菜单退出


        // ==================== 状态管理字段 ====================
        private string? _currentPlayStatus;           // 当前播放状态
        private byte[]? _currentImageRgb565Data;      // 当前图片的RGB565编码数据
        private string? _lastImageHash;               // 上次图片哈希（用于变化检测）


        public MainWindow()
        {
            InitializeComponent();

            // 初始化服务实例
            _mediaService = new MediaService();
            _trayService = new TrayService();


            // 初始化各模块
            InitializeMediaService();
            InitializeTrayService();
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

                _trayService.Initialize("系统媒体监听与串口控制", _app_icon);
                // 使用后清理临时文件（可选）
                // File.Delete(iconPath);
            }
            else
            {
                // 配置托盘服务
                _trayService.Initialize("系统媒体监听与串口控制");
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
            _trayService.ShowNotification("程序已最小化到托盘",
                "单击托盘图标可恢复窗口",
                System.Windows.Forms.ToolTipIcon.Info,
                1000);
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


    // ==================== 窗口事件处理 ====================

        // 窗口关闭事件
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
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
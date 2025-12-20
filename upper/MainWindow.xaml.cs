using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

using upper.Services;

namespace upper
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
    // ==================== 服务实例声明 ====================
        private readonly MediaService _mediaService;


    // ==================== 状态管理字段 ====================
        private string? _currentPlayStatus;           // 当前播放状态
        private byte[]? _currentImageRgb565Data;      // 当前图片的RGB565编码数据
        private string? _lastImageHash;               // 上次图片哈希（用于变化检测）


        public MainWindow()
        {
            InitializeComponent();

            // 初始化服务实例
            _mediaService = new MediaService();


            // 初始化各模块
            InitializeMediaService();
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







    }
}
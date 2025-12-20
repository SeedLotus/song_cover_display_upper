using System;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Control;

namespace upper.Services
{
    public class MediaService : IDisposable
    {
        // 核心对象
        private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;

        // 公开的事件，供主窗口订阅
        public event EventHandler<MediaInfoChangedEventArgs>? MediaInfoChanged;
        public event EventHandler<PlaybackStateChangedEventArgs>? PlaybackStateChanged;
        public event EventHandler<bool>? SessionAvailabilityChanged;

        // 携带媒体信息的事件参数
        public class MediaInfoChangedEventArgs : EventArgs
        {
            public string Title { get; set; } = string.Empty;
            public string Artist { get; set; } = string.Empty;
            public string Album { get; set; } = string.Empty;
            public object? ThumbnailStream { get; set; } // 封面图原始流，主窗口可转为BitmapImage
        }

        // 携带播放状态的事件参数
        public class PlaybackStateChangedEventArgs : EventArgs
        {
            public string State { get; set; } = string.Empty; // "Playing", "Paused", "Stopped"
            public bool IsPlaying => State == "Playing";
            public bool IsPaused => State == "Paused";
        }

        /// <summary>
        /// 初始化媒体服务，开始监听系统媒体变化
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                // 获取全局媒体会话管理器
                _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

                // 获取当前会话并设置监听
                await SetupCurrentSessionAsync();

                // 监听当前会话改变事件（用户切换了播放器）
                _sessionManager.CurrentSessionChanged += async (sender, args) =>
                {
                    await OnCurrentSessionChangedAsync();
                };

                OnSessionAvailabilityChanged(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediaService初始化失败: {ex.Message}");
                OnSessionAvailabilityChanged(false);
                throw; // 可以选择向上抛出或静默处理
            }
        }

        /// <summary>
        /// 设置当前媒体会话并监听其变化
        /// </summary>
        private async Task SetupCurrentSessionAsync()
        {
            if (_sessionManager == null) return;

            // 清理之前的会话监听
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            }

            // 获取新的当前会话
            _currentSession = _sessionManager.GetCurrentSession();

            if (_currentSession != null)
            {
                // 监听当前会话的变化
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;

                // 立即获取一次当前信息
                await UpdateMediaInfoAsync();
                await UpdatePlaybackInfoAsync();
            }
            else
            {
                // 没有活动会话，通知主窗口
                OnMediaInfoChanged(null);
                OnPlaybackStateChanged("NoSession");
            }
        }

        /// <summary>
        /// 当前会话改变时的处理
        /// </summary>
        private async Task OnCurrentSessionChangedAsync()
        {
            await SetupCurrentSessionAsync();
        }

        /// <summary>
        /// 媒体属性（歌曲信息）变化时的处理
        /// </summary>
        private async void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender,
            MediaPropertiesChangedEventArgs args)
        {
            await UpdateMediaInfoAsync();
        }

        /// <summary>
        /// 播放状态变化时的处理
        /// </summary>
        private async void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender,
            object args)
        {
            await UpdatePlaybackInfoAsync();
        }

        /// <summary>
        /// 更新媒体信息并触发事件
        /// </summary>
        private async Task UpdateMediaInfoAsync()
        {
            if (_currentSession == null) return;

            try
            {
                var mediaProperties = await _currentSession.TryGetMediaPropertiesAsync();

                var eventArgs = new MediaInfoChangedEventArgs
                {
                    Title = mediaProperties.Title ?? "未知标题",
                    Artist = mediaProperties.Artist ?? "未知艺术家",
                    Album = mediaProperties.AlbumTitle ?? "未知专辑",
                    ThumbnailStream = mediaProperties.Thumbnail
                };

                OnMediaInfoChanged(eventArgs);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取媒体信息失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新播放状态并触发事件
        /// </summary>
        private async Task UpdatePlaybackInfoAsync()
        {
            if (_currentSession == null) return;

            try
            {
                var playbackInfo = _currentSession.GetPlaybackInfo();
                string state = playbackInfo.PlaybackStatus.ToString();

                OnPlaybackStateChanged(state);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取播放状态失败: {ex.Message}");
            }
        }

        // ==================== 控制方法 ====================

        /// <summary>
        /// 尝试播放
        /// </summary>
        public async Task<bool> TryPlayAsync()
        {
            if (_currentSession == null) return false;

            try
            {
                return await _currentSession.TryPlayAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"播放控制失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 尝试暂停
        /// </summary>
        public async Task<bool> TryPauseAsync()
        {
            if (_currentSession == null) return false;

            try
            {
                return await _currentSession.TryPauseAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"暂停控制失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 尝试切换到下一曲
        /// </summary>
        public async Task<bool> TrySkipNextAsync()
        {
            if (_currentSession == null) return false;

            try
            {
                return await _currentSession.TrySkipNextAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"下一曲控制失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 尝试切换到上一曲
        /// </summary>
        public async Task<bool> TrySkipPreviousAsync()
        {
            if (_currentSession == null) return false;

            try
            {
                return await _currentSession.TrySkipPreviousAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"上一曲控制失败: {ex.Message}");
                return false;
            }
        }

        // ==================== 事件触发方法 ====================

        protected virtual void OnMediaInfoChanged(MediaInfoChangedEventArgs? args)
        {
            MediaInfoChanged?.Invoke(this, args ?? new MediaInfoChangedEventArgs());
        }

        protected virtual void OnPlaybackStateChanged(string state)
        {
            PlaybackStateChanged?.Invoke(this, new PlaybackStateChangedEventArgs { State = state });
        }

        protected virtual void OnSessionAvailabilityChanged(bool isAvailable)
        {
            SessionAvailabilityChanged?.Invoke(this, isAvailable);
        }

        // ==================== 资源清理 ====================

        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // 清理托管资源
                    if (_currentSession != null)
                    {
                        _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                        _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
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

        ~MediaService()
        {
            Dispose(disposing: false);
        }
    }
}
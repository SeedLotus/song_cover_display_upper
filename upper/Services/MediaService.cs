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
        private readonly object _sessionLock = new object(); // 添加锁来防止竞态条件

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
            public string State { get; set; } = string.Empty; // "Playing", "Paused", "Stopped", "Changing"
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
                // 可以选择不抛出，而是通知上层初始化失败
            }
        }

        /// <summary>
        /// 设置当前媒体会话并监听其变化
        /// </summary>
        private async Task SetupCurrentSessionAsync()
        {
            if (_sessionManager == null) return;

            GlobalSystemMediaTransportControlsSession? oldSession = null;
            GlobalSystemMediaTransportControlsSession? newSession = null;

            lock (_sessionLock)
            {
                // 保存旧会话引用
                oldSession = _currentSession;

                // 获取新的当前会话
                newSession = _sessionManager.GetCurrentSession();
                _currentSession = newSession;
            }

            // 清理旧会话监听（在锁外执行，避免死锁）
            if (oldSession != null)
            {
                await SafeUnsubscribeEvents(oldSession);
            }

            if (newSession != null)
            {
                try
                {
                    // 监听当前会话的变化
                    newSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                    newSession.PlaybackInfoChanged += OnPlaybackInfoChanged;

                    // 立即获取一次当前信息
                    await UpdateMediaInfoAsync();
                    await UpdatePlaybackInfoAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"设置新会话监听失败: {ex.Message}");
                    // 如果设置失败，清理_currentSession
                    lock (_sessionLock)
                    {
                        if (_currentSession == newSession)
                        {
                            _currentSession = null;
                        }
                    }
                }
            }
            else
            {
                // 没有活动会话，通知主窗口
                OnMediaInfoChanged(null);
                OnPlaybackStateChanged("NoSession");
            }
        }

        /// <summary>
        /// 安全地取消订阅会话事件
        /// </summary>
        private Task SafeUnsubscribeEvents(GlobalSystemMediaTransportControlsSession session)
        {
            if (session == null) return Task.CompletedTask;

            try
            {
                // 尝试取消订阅所有事件，如果会话已释放，会抛出异常
                session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            }
            catch (Exception ex)
            {
                // 这里捕获异常是因为会话可能已经被释放
                System.Diagnostics.Debug.WriteLine($"取消订阅会话事件失败（可能是会话已释放）: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 当前会话改变时的处理
        /// </summary>
        private async Task OnCurrentSessionChangedAsync()
        {
            try
            {
                await SetupCurrentSessionAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"处理会话变更时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 媒体属性（歌曲信息）变化时的处理
        /// </summary>
        private async void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender,
            MediaPropertiesChangedEventArgs args)
        {
            try
            {
                // 验证发送者是否仍然是当前会话
                lock (_sessionLock)
                {
                    if (sender != _currentSession)
                    {
                        // 如果不是当前会话的事件，忽略
                        return;
                    }
                }

                await UpdateMediaInfoAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"处理媒体属性变化时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 播放状态变化时的处理
        /// </summary>
        private async void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender,
            object args)
        {
            try
            {
                // 验证发送者是否仍然是当前会话
                lock (_sessionLock)
                {
                    if (sender != _currentSession)
                    {
                        // 如果不是当前会话的事件，忽略
                        return;
                    }
                }

                await UpdatePlaybackInfoAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"处理播放状态变化时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新媒体信息并触发事件
        /// </summary>
        private async Task UpdateMediaInfoAsync()
        {
            GlobalSystemMediaTransportControlsSession? currentSession;

            lock (_sessionLock)
            {
                currentSession = _currentSession;
            }

            if (currentSession == null) return;

            try
            {
                var mediaProperties = await currentSession.TryGetMediaPropertiesAsync();

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
            GlobalSystemMediaTransportControlsSession? currentSession;

            lock (_sessionLock)
            {
                currentSession = _currentSession;
            }

            if (currentSession == null) return;

            try
            {
                var playbackInfo = currentSession.GetPlaybackInfo();
                string state = playbackInfo.PlaybackStatus.ToString();

                OnPlaybackStateChanged(state);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取播放状态失败: {ex.Message}");
                // 如果获取失败，可能是会话已无效
                OnPlaybackStateChanged("NoSession");
            }
        }

        // ==================== 控制方法 ====================

        /// <summary>
        /// 尝试播放
        /// </summary>
        public async Task<bool> TryPlayAsync()
        {
            GlobalSystemMediaTransportControlsSession? currentSession;

            lock (_sessionLock)
            {
                currentSession = _currentSession;
            }

            if (currentSession == null) return false;

            try
            {
                return await currentSession.TryPlayAsync();
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
            GlobalSystemMediaTransportControlsSession? currentSession;

            lock (_sessionLock)
            {
                currentSession = _currentSession;
            }

            if (currentSession == null) return false;

            try
            {
                return await currentSession.TryPauseAsync();
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
            GlobalSystemMediaTransportControlsSession? currentSession;

            lock (_sessionLock)
            {
                currentSession = _currentSession;
            }

            if (currentSession == null) return false;

            try
            {
                return await currentSession.TrySkipNextAsync();
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
            GlobalSystemMediaTransportControlsSession? currentSession;

            lock (_sessionLock)
            {
                currentSession = _currentSession;
            }

            if (currentSession == null) return false;

            try
            {
                return await currentSession.TrySkipPreviousAsync();
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
            try
            {
                MediaInfoChanged?.Invoke(this, args ?? new MediaInfoChangedEventArgs());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"触发MediaInfoChanged事件时出错: {ex.Message}");
            }
        }

        protected virtual void OnPlaybackStateChanged(string state)
        {
            try
            {
                PlaybackStateChanged?.Invoke(this, new PlaybackStateChangedEventArgs { State = state });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"触发PlaybackStateChanged事件时出错: {ex.Message}");
            }
        }

        protected virtual void OnSessionAvailabilityChanged(bool isAvailable)
        {
            try
            {
                SessionAvailabilityChanged?.Invoke(this, isAvailable);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"触发SessionAvailabilityChanged事件时出错: {ex.Message}");
            }
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
                    GlobalSystemMediaTransportControlsSession? sessionToCleanup;

                    lock (_sessionLock)
                    {
                        sessionToCleanup = _currentSession;
                        _currentSession = null;
                    }

                    if (sessionToCleanup != null)
                    {
                        try
                        {
                            sessionToCleanup.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                            sessionToCleanup.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"清理会话事件时出错: {ex.Message}");
                        }
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

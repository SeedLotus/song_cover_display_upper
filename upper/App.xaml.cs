using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using upper.Services;

namespace upper
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        // 单实例互斥锁
        private const string UniqueMutexName = "realTiX.CoverDisplay.UniqueMutex";
        private static Mutex? _mutex;
        private static bool _isAnotherInstanceRunning;
        public static bool IsSecondInstance { get; private set; }

        // IPC 服务（命名管道，第二实例通知主实例弹窗）
        private IpcService? _ipcService;

        private bool _silentStart;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 检查是否已有实例运行
            CheckForExistingInstance();

            if (_isAnotherInstanceRunning)
            {
                IsSecondInstance = true;

                // 尝试通过命名管道通知主实例弹窗
                if (!IpcService.SendShowWindowSignal())
                {
                    Debug.WriteLine("已向主实例发送窗口恢复信号失败（主实例可能正在退出）");
                }

                // 立即关闭当前实例
                Shutdown();
                return;
            }

            IsSecondInstance = false;
            // 静默启动：命令行 --silent（开机自启快捷方式携带）或用户勾选了"静默启动"设置
            var settings = AppSettings.Load();
            _silentStart = e.Args.Contains("--silent", StringComparer.OrdinalIgnoreCase) || settings.SilentStart;

            // 这是第一个实例，正常启动
            base.OnStartup(e);

            MainWindow = new MainWindow();

            if (!_silentStart)
            {
                MainWindow.Show();
            }

            // 启动IPC服务器
            StartIpcServer();
        }

        private void StartIpcServer()
        {
            _ipcService = new IpcService();
            _ipcService.ShowWindowRequested += (s, e) =>
            {
                // 在UI线程上恢复窗口
                Dispatcher.Invoke(() => RestoreMainWindow());
            };
            _ipcService.StartServer();
        }

        private void CheckForExistingInstance()
        {
            bool createdNew;
            _mutex = new Mutex(true, UniqueMutexName, out createdNew);

            // 如果Mutex已存在，说明有另一个实例正在运行
            _isAnotherInstanceRunning = !createdNew;

            // 防止GC回收Mutex
            if (!_isAnotherInstanceRunning)
            {
                GC.KeepAlive(_mutex);
            }
        }

        public void RestoreMainWindow()
        {
            if (MainWindow == null) return;

            try
            {
                // 确保在UI线程上执行
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.Invoke(() => RestoreMainWindow());
                    return;
                }

                // 如果窗口被隐藏，显示它
                if (!MainWindow.IsVisible)
                {
                    MainWindow.Show();
                }

                // 如果窗口最小化，恢复它
                if (MainWindow.WindowState == WindowState.Minimized)
                {
                    MainWindow.WindowState = WindowState.Normal;
                }

                // 激活窗口并置前
                MainWindow.Activate();
                MainWindow.Topmost = true;
                MainWindow.Topmost = false;
                MainWindow.Focus();

                Debug.WriteLine("窗口已恢复");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"恢复窗口失败: {ex.Message}");
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 统一释放各服务资源（托盘图标、串口、媒体会话、IPC 管道）
            (MainWindow as MainWindow)?.CleanupServices();
            _ipcService?.Dispose();
            _mutex?.Close();
            base.OnExit(e);
        }

    }

}

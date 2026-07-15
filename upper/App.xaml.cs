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
        private static Mutex _mutex;
        private static bool _isAnotherInstanceRunning;
        public static bool IsSecondInstance { get; private set; }

        // IPC通信常量
        public const int WM_SHOW_APP = 0x0400 + 1; // 自定义消息
        private const int HWND_BROADCAST = 0xFFFF;

        private bool _silentStart;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 检查是否已有实例运行
            CheckForExistingInstance();

            if (_isAnotherInstanceRunning)
            {
                IsSecondInstance = true;

                // 尝试通过命名管道发送信号
                bool signalSent = IpcService.SendShowWindowSignal();

                // 立即关闭当前实例
                Shutdown();
                return;
            }

            IsSecondInstance = false;
            _silentStart = e.Args.Contains("--silent", StringComparer.OrdinalIgnoreCase);

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
            var ipcService = new IpcService();
            ipcService.ShowWindowRequested += (s, e) =>
            {
                // 在UI线程上恢复窗口
                Dispatcher.Invoke(() => RestoreMainWindow());
            };
            ipcService.StartServer();
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
            // 清理资源
            _mutex?.Close();
            base.OnExit(e);
        }

    }

}

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace upper.Services
{
    public class IpcService : IDisposable
    {
        private NamedPipeServerStream _pipeServer;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isDisposed;

        public event EventHandler ShowWindowRequested;

        public void StartServer()
        {
            _cancellationTokenSource = new CancellationTokenSource();

            Task.Run(async () =>
            {
                while (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        _pipeServer = new NamedPipeServerStream(
                            "realTiX.CoverDisplay.ShowWindowPipe",
                            PipeDirection.In,
                            1,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);

                        // 等待客户端连接
                        await _pipeServer.WaitForConnectionAsync(_cancellationTokenSource.Token);

                        // 读取数据
                        using (var reader = new StreamReader(_pipeServer))
                        {
                            var message = await reader.ReadToEndAsync();
                            if (message == "SHOW_WINDOW")
                            {
                                // 触发显示窗口事件
                                ShowWindowRequested?.Invoke(this, EventArgs.Empty);
                            }
                        }

                        // 断开连接
                        _pipeServer.Disconnect();
                        _pipeServer.Dispose();
                        _pipeServer = null;
                    }
                    catch (OperationCanceledException)
                    {
                        // 正常取消
                        break;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"IPC服务错误: {ex.Message}");
                        Thread.Sleep(1000); // 等待后重试
                    }
                }
            });
        }

        public static bool SendShowWindowSignal()
        {
            try
            {
                using (var pipeClient = new NamedPipeClientStream(
                    ".",
                    "realTiX.CoverDisplay.ShowWindowPipe",
                    PipeDirection.Out))
                {
                    // 尝试连接，超时时间短
                    pipeClient.Connect(100);

                    using (var writer = new StreamWriter(pipeClient))
                    {
                        writer.Write("SHOW_WINDOW");
                        writer.Flush();
                    }

                    return true;
                }
            }
            catch (TimeoutException)
            {
                // 没有服务在运行
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"发送IPC消息失败: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _cancellationTokenSource?.Cancel();
                _pipeServer?.Dispose();
                _isDisposed = true;
            }
        }
    }
}
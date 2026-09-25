using System;
using System.IO;
using System.Text;

namespace upper.Services
{
    /// <summary>
    /// 轻量滚动文件日志：exe 同目录 logs/upper-yyyyMMdd.log，保留最近 7 天。
    /// 用于 Release 包实机抓 SMTC 事件序列（3.7 网易云问题排查），同时镜像到 Debug 输出。
    /// 只记录事件驱动的关键节点；高频路径（/h 心跳、批量发包）不记录，避免刷屏。
    /// </summary>
    public static class FileLogger
    {
        private static readonly object _lock = new();
        private static string? _logFilePath;
        private static bool _initFailed;

        private static string? GetLogFilePath()
        {
            lock (_lock)
            {
                if (_initFailed) return null;
                if (_logFilePath != null) return _logFilePath;

                try
                {
                    string baseDir = AppContext.BaseDirectory;
                    string logDir = Path.Combine(baseDir, "logs");
                    Directory.CreateDirectory(logDir);
                    _logFilePath = Path.Combine(logDir, $"upper-{DateTime.Now:yyyyMMdd}.log");
                    CleanupOldLogs(logDir, keepDays: 7);
                }
                catch (Exception ex)
                {
                    _initFailed = true;
                    System.Diagnostics.Debug.WriteLine($"[Log] 日志初始化失败: {ex.Message}");
                }

                return _logFilePath;
            }
        }

        /// <summary>
        /// 写一行日志（带毫秒时间戳），同时镜像到 Debug 输出。任何失败静默吞掉，不影响主流程。
        /// </summary>
        public static void Log(string tag, string message)
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} [{tag}] {message}";
            System.Diagnostics.Debug.WriteLine(line);

            try
            {
                string? path = GetLogFilePath();
                if (path == null) return;

                lock (_lock)
                {
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // 日志写入失败（磁盘满/权限等）绝不能影响业务
            }
        }

        private static void CleanupOldLogs(string logDir, int keepDays)
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-keepDays);
                foreach (var file in Directory.GetFiles(logDir, "upper-*.log"))
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
            }
            catch
            {
                // 清理失败无所谓
            }
        }
    }
}

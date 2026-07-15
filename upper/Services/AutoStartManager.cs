using System;
using System.IO;
using System.Linq;
using System.Diagnostics;

namespace upper.Services
{
    /// <summary>
    /// 开机自启动管理器 - 通过创建/删除启动文件夹快捷方式实现
    /// 修正版：确保始终指向正确的EXE文件
    /// </summary>
    public class AutoStartManager
    {
        private string _appName;
        private string _shortcutName;
        private string _shortcutPath;

        /// <summary>
        /// 应用程序可执行文件路径（确保是EXE）
        /// </summary>
        public string AppExecutablePath { get; private set; }

        /// <summary>
        /// 是否已启用开机自启动
        /// </summary>
        public bool IsEnabled { get; private set; }

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="appName">应用程序名称（用于快捷方式显示）</param>
        public AutoStartManager(string appName = "唱片机控制器")
        {
            _appName = appName;

            // 智能查找EXE文件路径
            AppExecutablePath = FindExeFilePath();

            if (string.IsNullOrEmpty(AppExecutablePath))
            {
                throw new FileNotFoundException("无法找到应用程序的EXE文件");
            }

            // 构建快捷方式名称和路径
            _shortcutName = $"{Path.GetFileNameWithoutExtension(AppExecutablePath)}.lnk";

            // 获取当前用户的启动文件夹路径
            string startupFolder = GetStartupFolderPath();
            _shortcutPath = Path.Combine(startupFolder, _shortcutName);

            // 检测当前状态
            CheckStatus();
        }

        /// <summary>
        /// 智能查找EXE文件路径
        /// </summary>
        private string FindExeFilePath()
        {
            string[] possiblePaths =
            {
                // 1. 首先尝试直接获取当前进程的主模块
                Process.GetCurrentProcess().MainModule?.FileName,
                
                // 2. 尝试Assembly的Location（可能返回DLL）
                System.Reflection.Assembly.GetEntryAssembly()?.Location,
                
                // 3. 当前目录下的EXE文件
                Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.exe")
                    .FirstOrDefault(f => !f.Contains(".vshost")), // 排除vshost调试文件
            };

            foreach (var path in possiblePaths)
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path) &&
                    Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"找到EXE文件: {path}");
                    return Path.GetFullPath(path);
                }
            }

            // 4. 如果上面都失败了，手动搜索
            string currentDir = AppDomain.CurrentDomain.BaseDirectory;
            var exeFiles = Directory.GetFiles(currentDir, "*.exe")
                .Where(f => !f.Contains(".vshost"))
                .ToList();

            if (exeFiles.Count == 1)
            {
                return Path.GetFullPath(exeFiles[0]);
            }
            else if (exeFiles.Count > 1)
            {
                // 如果有多个EXE，优先选择与程序集同名的
                string assemblyName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
                if (!string.IsNullOrEmpty(assemblyName))
                {
                    var matchingExe = exeFiles.FirstOrDefault(f =>
                        Path.GetFileNameWithoutExtension(f).Equals(assemblyName, StringComparison.OrdinalIgnoreCase));

                    if (matchingExe != null)
                    {
                        return Path.GetFullPath(matchingExe);
                    }
                }

                // 返回第一个不是vshost的EXE
                return Path.GetFullPath(exeFiles.FirstOrDefault(f => !f.Contains(".vshost")) ?? exeFiles[0]);
            }

            throw new FileNotFoundException("在当前目录下找不到EXE文件");
        }

        /// <summary>
        /// 验证EXE文件是否有效
        /// </summary>
        private bool ValidateExeFile(string exePath)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                return false;
            }

            // 检查文件扩展名
            if (!Path.GetExtension(exePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 检查文件大小（合理的EXE文件应该有一定大小）
            var fileInfo = new FileInfo(exePath);
            if (fileInfo.Length < 1024) // 小于1KB可能不是有效EXE
            {
                return false;
            }

            // 可以进一步检查文件签名等，这里简化处理
            return true;
        }

        /// <summary>
        /// 获取当前用户的启动文件夹路径
        /// </summary>
        private string GetStartupFolderPath()
        {
            try
            {
                string startupPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);

                if (!Directory.Exists(startupPath))
                {
                    Directory.CreateDirectory(startupPath);
                }

                return startupPath;
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"无法访问启动文件夹: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 检测当前开机自启动状态
        /// </summary>
        public bool CheckStatus()
        {
            try
            {
                // 检查快捷方式是否存在
                IsEnabled = File.Exists(_shortcutPath);

                if (IsEnabled)
                {
                    // 这里可以添加对快捷方式目标的有效性检查
                    // 但由于解析.lnk文件比较复杂，暂时不实现
                }

                return IsEnabled;
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"检测开机自启动状态失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 启用开机自启动（创建快捷方式）
        /// </summary>
        public bool EnableAutoStart()
        {
            try
            {
                // 重新验证EXE文件
                if (!ValidateExeFile(AppExecutablePath))
                {
                    throw new FileNotFoundException($"无效的应用程序文件: {AppExecutablePath}");
                }

                // 删除已存在的快捷方式（如果存在）
                if (File.Exists(_shortcutPath))
                {
                    File.Delete(_shortcutPath);
                }

                // 使用标准方法创建快捷方式
                CreateShortcut(_shortcutPath, AppExecutablePath, _appName);

                // 验证是否创建成功
                IsEnabled = File.Exists(_shortcutPath);

                if (!IsEnabled)
                {
                    throw new ApplicationException("快捷方式创建失败");
                }

                Debug.WriteLine($"开机自启动已启用，快捷方式指向: {AppExecutablePath}");
                return true;
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"启用开机自启动失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 禁用开机自启动（删除快捷方式）
        /// </summary>
        public bool DisableAutoStart()
        {
            try
            {
                // 检查快捷方式是否存在
                if (!File.Exists(_shortcutPath))
                {
                    IsEnabled = false;
                    return true;
                }

                // 尝试删除快捷方式
                File.Delete(_shortcutPath);

                // 验证是否删除成功
                IsEnabled = File.Exists(_shortcutPath);

                if (IsEnabled)
                {
                    throw new ApplicationException("快捷方式删除失败，文件仍然存在");
                }

                Debug.WriteLine("开机自启动已禁用");
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                throw new ApplicationException("权限不足，无法删除快捷方式");
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"禁用开机自启动失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 创建快捷方式（使用Windows Script Host Shell）
        /// </summary>
        private void CreateShortcut(string shortcutPath, string targetPath, string description)
        {
            // 创建PowerShell命令来创建快捷方式
            string powerShellCommand = $@"
                $WshShell = New-Object -ComObject WScript.Shell
                $Shortcut = $WshShell.CreateShortcut('{shortcutPath.Replace("'", "''")}')
                $Shortcut.TargetPath = '{targetPath.Replace("'", "''")}'
                $Shortcut.WorkingDirectory = '{Path.GetDirectoryName(targetPath).Replace("'", "''")}'
                $Shortcut.Arguments = '--silent'
                $Shortcut.Description = '{description.Replace("'", "''")}'
                $Shortcut.IconLocation = '{targetPath.Replace("'", "''")},0'
                $Shortcut.Save()
            ";

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-Command \"{powerShellCommand}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using (var process = Process.Start(startInfo))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);

                if (process.ExitCode != 0)
                {
                    throw new ApplicationException($"创建快捷方式失败: {error}");
                }

                if (!string.IsNullOrEmpty(output))
                {
                    Debug.WriteLine($"PowerShell输出: {output}");
                }
            }

            // 验证快捷方式是否创建成功
            if (!File.Exists(shortcutPath))
            {
                throw new ApplicationException("快捷方式文件未创建成功");
            }
        }

        /// <summary>
        /// 获取启动文件夹的物理路径（用于显示给用户）
        /// </summary>
        public string GetStartupFolderDisplayPath()
        {
            try
            {
                string path = GetStartupFolderPath();
                return Path.GetFullPath(path);
            }
            catch
            {
                return "无法访问启动文件夹";
            }
        }

        /// <summary>
        /// 获取当前EXE文件信息（用于调试）
        /// </summary>
        public string GetExeFileInfo()
        {
            if (string.IsNullOrEmpty(AppExecutablePath))
            {
                return "未找到EXE文件";
            }

            try
            {
                var fileInfo = new FileInfo(AppExecutablePath);
                return $"{Path.GetFileName(AppExecutablePath)} ({fileInfo.Length / 1024} KB, 修改时间: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm})";
            }
            catch
            {
                return AppExecutablePath;
            }
        }
    }
}
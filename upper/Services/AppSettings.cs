using System.IO;
using System.Text.Json;

namespace upper.Services
{
    /// <summary>
    /// 应用设置持久化 - JSON 文件保存在 exe 同目录（便携模式）。
    /// 读取失败时静默回退到默认值，绝不影响启动。
    /// </summary>
    public class AppSettings
    {
        /// <summary>
        /// 静默启动：勾选后无论开机自启还是手动启动，都不弹出主窗口，仅显示托盘图标。
        /// </summary>
        public bool SilentStart { get; set; } = false;

        private static string SettingsFilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                string path = SettingsFilePath;
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"设置文件读取失败，使用默认值: {ex.Message}");
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"设置文件保存失败: {ex.Message}");
            }
        }
    }
}

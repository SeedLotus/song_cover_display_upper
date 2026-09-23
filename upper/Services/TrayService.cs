using System;
using System.Windows.Forms;
using System.Drawing;

namespace upper.Services
{
    public class TrayService : IDisposable
    {
        // 直接使用类型名，无需冗长的完全限定名
        private NotifyIcon? _notifyIcon;
        private bool _isDisposed;

        public event EventHandler? TrayIconDoubleClick;
        public event EventHandler? ExitRequested;
        public event EventHandler? OpenUrlRequested;
        public event EventHandler? AutoStartToggleRequested;
        public event EventHandler? SilentStartToggleRequested;

        private ToolStripMenuItem? _autoStartItem;
        private ToolStripMenuItem? _silentStartItem;

        public void Initialize(string tooltipText, Icon? customIcon = null)
        {
            if (_notifyIcon != null) return;

            _notifyIcon = new NotifyIcon
            {
                Icon = customIcon ?? SystemIcons.Application, // 使用系统图标作为默认值
                Text = tooltipText,
                Visible = true
            };

            // 配置事件：双击呼出主窗口（单击易误触，且与双击会重复触发）
            _notifyIcon.MouseDoubleClick += OnNotifyIconMouseDoubleClick;

            // 创建右键菜单
            CreateContextMenu();
        }

        private void CreateContextMenu()
        {
            if (_notifyIcon == null) return;

            var menu = new ContextMenuStrip();

            var showItem = new ToolStripMenuItem("显示主窗口");
            showItem.Click += (s, e) => TrayIconDoubleClick?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(showItem);

            // 可勾选项：开机自启动（勾选状态由 MainWindow 同步）
            _autoStartItem = new ToolStripMenuItem("开机自启动") { CheckOnClick = true };
            _autoStartItem.Click += (s, e) => AutoStartToggleRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(_autoStartItem);

            // 可勾选项：静默启动（勾选状态由 MainWindow 同步）
            _silentStartItem = new ToolStripMenuItem("静默启动")
            {
                CheckOnClick = true,
                ToolTipText = "勾选后，无论开机自启还是手动启动都不弹出主窗口，仅显示托盘图标"
            };
            _silentStartItem.Click += (s, e) => SilentStartToggleRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(_silentStartItem);

            var websiteItem = new ToolStripMenuItem("作者B站");
            websiteItem.Click += (s, e) => OpenUrlRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(websiteItem);

            // 分隔线
            menu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += (s, e) => ExitRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(exitItem);

            _notifyIcon.ContextMenuStrip = menu;
        }

        private void OnNotifyIconMouseDoubleClick(object? sender, MouseEventArgs e)
        {
            // 仅响应鼠标左键双击
            if (e.Button == MouseButtons.Left)
            {
                TrayIconDoubleClick?.Invoke(this, EventArgs.Empty);
            }
        }

        public void ShowNotification(string title, string text, ToolTipIcon icon = ToolTipIcon.Info, int timeout = 1000)
        {
            _notifyIcon?.ShowBalloonTip(timeout, title, text, icon);
        }

        public void UpdateTooltip(string text)
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Text = text;
            }
        }

        /// <summary>
        /// 同步"开机自启动"菜单项的勾选状态（以主窗口/AutoStartManager 的权威状态为准）
        /// </summary>
        public void SetAutoStartChecked(bool isChecked)
        {
            if (_autoStartItem != null)
            {
                _autoStartItem.Checked = isChecked;
            }
        }

        /// <summary>
        /// 同步"静默启动"菜单项的勾选状态（以 AppSettings 的权威状态为准）
        /// </summary>
        public void SetSilentStartChecked(bool isChecked)
        {
            if (_silentStartItem != null)
            {
                _silentStartItem.Checked = isChecked;
            }
        }

        // 新增：获取托盘图标句柄（用于激活窗口）
        //public IntPtr GetTrayIconHandle()
        //{
        //    return _notifyIcon?.Handle ?? IntPtr.Zero;
        //}

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    // 清理托管资源
                    _notifyIcon?.Dispose();
                }
                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        ~TrayService()
        {
            Dispose(disposing: false);
        }
    }
}

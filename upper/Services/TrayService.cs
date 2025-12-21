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

        public event EventHandler? TrayIconLeftClick;
        public event EventHandler? ExitRequested;
        public event EventHandler? OpenUrlRequested;

        public void Initialize(string tooltipText, Icon? customIcon = null)
        {
            if (_notifyIcon != null) return;

            _notifyIcon = new NotifyIcon
            {
                Icon = customIcon ?? SystemIcons.Application, // 使用系统图标作为默认值
                Text = tooltipText,
                Visible = true
            };

            // 配置事件
            _notifyIcon.MouseClick += OnNotifyIconMouseClick;

            // 创建右键菜单
            CreateContextMenu();
        }

        private void CreateContextMenu()
        {
            if (_notifyIcon == null) return;

            var menu = new ContextMenuStrip();

            var showItem = new ToolStripMenuItem("显示主窗口");
            showItem.Click += (s, e) => TrayIconLeftClick?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(showItem);

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

        private void OnNotifyIconMouseClick(object? sender, MouseEventArgs e)
        {
            // 仅响应鼠标左键单击
            if (e.Button == MouseButtons.Left)
            {
                TrayIconLeftClick?.Invoke(this, EventArgs.Empty);
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

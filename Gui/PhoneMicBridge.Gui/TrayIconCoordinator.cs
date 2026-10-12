using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace PhoneMicBridge.Gui
{
    /// <summary>
    /// Coordinates the lifecycle of the System Tray NotifyIcon,
    /// window visibility transitions (minimize/close to tray, restore),
    /// and explicit application exit.
    /// </summary>
    public class TrayIconCoordinator : IDisposable
    {
        private readonly Window _window;
        private readonly Action _onExplicitExit;
        private WinForms.NotifyIcon? _notifyIcon;
        private bool _isDisposed;

        public bool IsExplicitExit { get; private set; }

        public TrayIconCoordinator(Window window, Action onExplicitExit)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _onExplicitExit = onExplicitExit ?? throw new ArgumentNullException(nameof(onExplicitExit));

            InitializeNotifyIcon();
        }

        private void InitializeNotifyIcon()
        {
            var components = new Container();
            _notifyIcon = new WinForms.NotifyIcon(components)
            {
                Icon = SystemIcons.Application,
                Text = "Phone Mic Bridge",
                Visible = true
            };

            _notifyIcon.DoubleClick += (s, e) => RestoreWindow();

            var contextMenu = new WinForms.ContextMenuStrip();
            contextMenu.Items.Add("Show Bridge", null, (s, e) => RestoreWindow());
            contextMenu.Items.Add("Exit", null, (s, e) => RequestExplicitExit());

            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        public void RestoreWindow()
        {
            _window.Show();
            _window.ShowInTaskbar = true;
            _window.WindowState = WindowState.Normal;
            _window.Activate();
        }

        public void HideToTray()
        {
            _window.ShowInTaskbar = false;
            _window.Visibility = Visibility.Hidden;
        }

        public void HandleWindowStateChanged()
        {
            if (_window.WindowState == WindowState.Minimized)
            {
                HideToTray();
            }
        }

        public void HandleWindowClosing(CancelEventArgs e)
        {
            if (!IsExplicitExit)
            {
                e.Cancel = true;
                HideToTray();
            }
        }

        public void RequestExplicitExit()
        {
            IsExplicitExit = true;
            _onExplicitExit();
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
        }
    }
}

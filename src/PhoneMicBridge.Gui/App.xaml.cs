using System;
using System.Windows;
using PhoneMicBridge.Core;

namespace PhoneMicBridge.Gui
{
    public partial class App : System.Windows.Application
    {
        private ISingleInstanceGuard? _singleInstanceGuard;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _singleInstanceGuard = new SingleInstanceGuard();
            if (!_singleInstanceGuard.TryAcquire())
            {
                MessageBox.Show(
                    "Another instance of Phone Mic Bridge is already running.\nOnly one instance is allowed at a time.",
                    "Phone Mic Bridge",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                Shutdown();
                return;
            }

            var mainWindow = new MainWindow();
            mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _singleInstanceGuard?.Dispose();
            _singleInstanceGuard = null;
            base.OnExit(e);
        }
    }
}

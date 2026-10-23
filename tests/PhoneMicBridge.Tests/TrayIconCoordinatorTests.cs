using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using Xunit;

namespace PhoneMicBridge.Tests
{
    /// <summary>
    /// Verification test suite for SPEC-008:
    /// Minimize to Tray, Close to Tray, TrayIconCoordinator lifecycle, and Shell Integration.
    /// </summary>
    public class TrayIconCoordinatorTests
    {
        private static Type GetTrayCoordinatorType()
        {
            string testDir = AppContext.BaseDirectory;
            string guiDllPath = Path.GetFullPath(Path.Combine(testDir, @"..\..\..\..\..\src\PhoneMicBridge.Gui\bin\Debug\net9.0-windows10.0.19041.0\PhoneMicBridge.Gui.dll"));

            if (!File.Exists(guiDllPath))
            {
                string solutionRoot = Path.GetFullPath(Path.Combine(testDir, @"..\..\..\..\..\"));
                guiDllPath = Path.Combine(solutionRoot, "src", "PhoneMicBridge.Gui", "bin", "Debug", "net9.0-windows", "PhoneMicBridge.Gui.dll");
            }

            Assert.True(File.Exists(guiDllPath), $"PhoneMicBridge.Gui.dll must exist at: {guiDllPath}");

            Assembly guiAssembly = Assembly.LoadFrom(guiDllPath);
            Type? type = guiAssembly.GetType("PhoneMicBridge.Gui.TrayIconCoordinator");
            Assert.NotNull(type);
            return type;
        }

        private static void RunInSta(Action action)
        {
            Exception? caught = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (caught != null)
            {
                throw new AggregateException("STA thread execution failed", caught);
            }
        }

        [Fact]
        public void MinimizeEvent_TriggersHideToTray_ShowInTaskbarFalse_AndVisibilityHidden()
        {
            RunInSta(() =>
            {
                var window = new Window();
                bool exitCalled = false;
                var coordinatorType = GetTrayCoordinatorType();

                using var coordinator = (IDisposable)Activator.CreateInstance(
                    coordinatorType,
                    new object[] { window, (Action)(() => exitCalled = true) }
                )!;

                window.WindowState = WindowState.Minimized;

                var handleStateChangedMethod = coordinatorType.GetMethod("HandleWindowStateChanged");
                Assert.NotNull(handleStateChangedMethod);
                handleStateChangedMethod.Invoke(coordinator, null);

                Assert.False(window.ShowInTaskbar);
                Assert.Equal(Visibility.Hidden, window.Visibility);
                Assert.False(exitCalled);
            });
        }

        [Fact]
        public void NormalCloseEvent_CancelsClose_HidesWindowToTray_AndMaintainsProcess()
        {
            RunInSta(() =>
            {
                var window = new Window
                {
                    Visibility = Visibility.Visible,
                    ShowInTaskbar = true
                };

                bool exitCalled = false;
                var coordinatorType = GetTrayCoordinatorType();

                using var coordinator = (IDisposable)Activator.CreateInstance(
                    coordinatorType,
                    new object[] { window, (Action)(() => exitCalled = true) }
                )!;

                var cancelArgs = new CancelEventArgs(false);
                var handleClosingMethod = coordinatorType.GetMethod("HandleWindowClosing");
                Assert.NotNull(handleClosingMethod);
                handleClosingMethod.Invoke(coordinator, new object[] { cancelArgs });

                Assert.True(cancelArgs.Cancel, "Closing must be cancelled when not an explicit exit");
                Assert.False(window.ShowInTaskbar);
                Assert.Equal(Visibility.Hidden, window.Visibility);
                Assert.False(exitCalled);
            });
        }

        [Fact]
        public void RestoreWindow_SetsShowInTaskbarTrue_WindowStateNormal_AndShowsWindow()
        {
            RunInSta(() =>
            {
                var window = new Window
                {
                    Visibility = Visibility.Hidden,
                    ShowInTaskbar = false,
                    WindowState = WindowState.Minimized
                };

                var coordinatorType = GetTrayCoordinatorType();
                using var coordinator = (IDisposable)Activator.CreateInstance(
                    coordinatorType,
                    new object[] { window, (Action)(() => { }) }
                )!;

                var restoreMethod = coordinatorType.GetMethod("RestoreWindow");
                Assert.NotNull(restoreMethod);
                restoreMethod.Invoke(coordinator, null);

                Assert.True(window.ShowInTaskbar);
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.Equal(Visibility.Visible, window.Visibility);
            });
        }

        [Fact]
        public void RequestExplicitExit_InvokesExitAction_AndAllowsCloseWithoutCancel()
        {
            RunInSta(() =>
            {
                var window = new Window();
                bool exitCalled = false;
                var coordinatorType = GetTrayCoordinatorType();

                using var coordinator = (IDisposable)Activator.CreateInstance(
                    coordinatorType,
                    new object[] { window, (Action)(() => exitCalled = true) }
                )!;

                var explicitExitMethod = coordinatorType.GetMethod("RequestExplicitExit");
                Assert.NotNull(explicitExitMethod);
                explicitExitMethod.Invoke(coordinator, null);

                Assert.True(exitCalled, "Explicit exit callback must be invoked");

                var cancelArgs = new CancelEventArgs(false);
                var handleClosingMethod = coordinatorType.GetMethod("HandleWindowClosing");
                Assert.NotNull(handleClosingMethod);
                handleClosingMethod.Invoke(coordinator, new object[] { cancelArgs });

                Assert.False(cancelArgs.Cancel, "Closing must NOT be cancelled during explicit exit");
            });
        }

        [Fact]
        public void Dispose_DisposesNotifyIconWithoutThrowing_AndIsIdempotent()
        {
            RunInSta(() =>
            {
                var window = new Window();
                var coordinatorType = GetTrayCoordinatorType();

                var coordinator = (IDisposable)Activator.CreateInstance(
                    coordinatorType,
                    new object[] { window, (Action)(() => { }) }
                )!;

                // Multiple dispose calls must be safe and idempotent
                coordinator.Dispose();
                coordinator.Dispose();
            });
        }
    }
}

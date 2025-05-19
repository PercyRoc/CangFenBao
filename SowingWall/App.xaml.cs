using System.Windows;
using SowingWall.Views;
using SowingWall.ViewModels;
using SowingWall.Views.Settings;
using SowingWall.Services;
using SowingWall.ViewModels.Settings;

namespace SowingWall
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App
    {
        private static Mutex? _mutex;
        private const string MutexName = "Global\\SowingWall_App_Mutex_2A7B9C1D-F3E5-4A8B-8D0C-9E1F2A3B4C5D";

        protected override Window CreateShell()
        {
            _mutex = new Mutex(true, MutexName, out var createdNew);

            if (createdNew)
            {
                return Container.Resolve<MainWindow>();
            }
            else
            {
                MessageBox.Show("智能播种墙系统已经在运行中。", "应用程序已启动", MessageBoxButton.OK, MessageBoxImage.Information);
                Current.Shutdown();
                return null!;
            }
        }

        protected override void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterForNavigation<MainWindow, MainViewModel>();
            containerRegistry.RegisterDialog<SettingsDialog, SettingsViewModel>();
            containerRegistry.RegisterForNavigation<WangDianTongSettingsView, WangDianTongSettingsViewModel>();
            containerRegistry.RegisterSingleton<IWangDianTongService, WangDianTongService>();
            containerRegistry.RegisterSingleton<ISowingWallPlcService, SowingWallPlcService>();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            _mutex = null;
            base.OnExit(e);
        }
    }
}

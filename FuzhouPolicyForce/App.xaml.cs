using System.IO;
using System.Windows;
using System.Windows.Threading;
using Common.Extensions;
using Common.Models.Settings.Sort.PendulumSort;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.Extensions;
using FuzhouPolicyForce.ViewModels;
using FuzhouPolicyForce.Views;
using FuzhouPolicyForce.Views.Settings;
using Microsoft.Extensions.Hosting;
using Prism.Ioc;
using Serilog;
using SharedUI.Extensions;
using SharedUI.ViewModels;
using SharedUI.ViewModels.Settings;
using SharedUI.Views;
using SortingServices.Pendulum;
using SortingServices.Pendulum.Extensions;
using Timer = System.Timers.Timer;

namespace FuzhouPolicyForce;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
internal partial class App
{
    private Timer? _cleanupTimer;

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册视图和ViewModel
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();

        // 注册公共服务
        containerRegistry.AddCommonServices();
        containerRegistry.AddShardUi();
        containerRegistry.AddPhotoCamera();

        // 注册设置页面的ViewModel
        containerRegistry.RegisterForNavigation<BalanceSortSettingsView, BalanceSortSettingsViewModel>();
        containerRegistry.RegisterForNavigation<BarcodeChuteSettingsView, BarcodeChuteSettingsViewModel>();

        containerRegistry.Register<Window, SettingsDialog>("SettingsDialog");
        containerRegistry.Register<SettingsDialogViewModel>();
        containerRegistry.Register<Window, HistoryWindow>("HistoryDialog");
        containerRegistry.Register<HistoryWindowViewModel>();

        // 获取设置服务
        var settingsService = Container.Resolve<ISettingsService>();

        // 注册多摆轮分拣服务
        containerRegistry.RegisterPendulumSortService(settingsService, PendulumServiceType.Multi);
        containerRegistry.RegisterSingleton<IHostedService, PendulumSortHostedService>();
    }

    protected override Window CreateShell()
    {
        return Container.Resolve<MainWindow>();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 配置Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.Debug()
            .WriteTo.File("logs/app-.log",
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true)
            .CreateLogger();

        // 注册全局异常处理
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        // 启动DUMP文件清理任务
        StartCleanupTask();

        Log.Information("应用程序启动");
        base.OnStartup(e);

        try
        {
            // 启动相机托管服务
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            cameraStartupService.StartAsync(CancellationToken.None).Wait();
            Log.Information("相机托管服务启动成功");

            // 启动摆轮分拣托管服务
            var pendulumHostedService = Container.Resolve<IHostedService>();
            pendulumHostedService.StartAsync(CancellationToken.None).Wait();
            Log.Information("摆轮分拣托管服务启动成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动托管服务时发生错误");
            throw;
        }
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Log.Fatal(e.Exception, "UI线程发生未处理的异常");
            MessageBox.Show($"程序发生错误：{e.Exception.Message}\n请查看日志了解详细信息。", "错误", MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "处理UI异常时发生错误");
        }
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var exception = e.ExceptionObject as Exception;
            Log.Fatal(exception, "应用程序域发生未处理的异常");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "处理应用程序域异常时发生错误");
        }
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            Log.Fatal(e.Exception, "异步任务中发生未处理的异常");
            e.SetObserved();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "处理异步任务异常时发生错误");
        }
    }

    /// <summary>
    ///     启动定期清理任务
    /// </summary>
    private void StartCleanupTask()
    {
        try
        {
            _cleanupTimer = new Timer(1000 * 60 * 60); // 每1小时执行一次
            _cleanupTimer.Elapsed += static (_, _) =>
            {
                try
                {
                    CleanupDumpFiles();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "清理DUMP文件时发生错误");
                }
            };
            _cleanupTimer.Start();

            // 应用启动时立即执行一次清理
            Task.Run(static () =>
            {
                try
                {
                    CleanupDumpFiles();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "初始清理DUMP文件时发生错误");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动清理任务时发生错误");
        }
    }

    /// <summary>
    ///     清理DUMP文件
    /// </summary>
    private static void CleanupDumpFiles()
    {
        try
        {
            var dumpPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory);
            var dumpFiles = Directory.GetFiles(dumpPath, "*.dmp", SearchOption.TopDirectoryOnly);

            var deletedCount = 0;
            foreach (var file in dumpFiles)
                try
                {
                    File.Delete(file);
                    deletedCount++;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "删除DUMP文件失败: {FilePath}", file);
                }

            if (deletedCount > 0) Log.Information("成功清理 {Count} 个DUMP文件", deletedCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "清理DUMP文件过程中发生错误");
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Information("应用程序开始关闭...");

            // 停止清理定时器
            if (_cleanupTimer != null)
            {
                _cleanupTimer.Stop();
                _cleanupTimer.Dispose();
                Log.Information("清理定时器已停止");
            }

            // 停止摆轮分拣托管服务
            var pendulumHostedService = Container.Resolve<IHostedService>();
            pendulumHostedService.StopAsync(CancellationToken.None).Wait();
            Log.Information("摆轮分拣托管服务已停止");

            // 停止相机托管服务
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            cameraStartupService.StopAsync(CancellationToken.None).Wait();
            Log.Information("相机托管服务已停止");

            // 释放相机工厂
            var cameraFactory = Container.Resolve<CameraFactory>();
            cameraFactory.Dispose();
            Log.Information("相机工厂已释放");

            // 释放相机服务
            var cameraService = Container.Resolve<ICameraService>();
            cameraService.Dispose();
            Log.Information("相机服务已释放");

            // 等待所有日志写入完成
            Log.Information("应用程序关闭");
            Log.CloseAndFlush();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "应用程序关闭时发生错误");
            Log.CloseAndFlush();
        }
        finally
        {
            base.OnExit(e);
        }
    }
}
using System.Net.Http;
using System.Windows;
using ChongqingYekelai.Services;
using ChongqingYekelai.ViewModels;
using ChongqingYekelai.ViewModels.Settings;
using ChongqingYekelai.Views;
using ChongqingYekelai.Views.Settings;
using Common.Extensions;
using Common.Models.Settings.Sort.PendulumSort;
using Common.Services.License;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using DeviceService.Extensions;
using Microsoft.Extensions.Hosting;
using Prism.Ioc;
using Serilog;
using SharedUI.Extensions;
using SharedUI.ViewModels.Settings;
using SharedUI.Views.Settings;
using SortingServices.Pendulum;
using SortingServices.Pendulum.Extensions;

namespace ChongqingYekelai;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
internal partial class App
{
    private static Mutex? _mutex;
    private const string MutexName = "ChongqingJushuitan_App_Mutex";

    /// <summary>
    ///     创建主窗口
    /// </summary>
    protected override Window CreateShell()
    {
        // 检查是否已经运行
        _mutex = new Mutex(true, MutexName, out var createdNew);

        if (createdNew) return Container.Resolve<MainWindow>();

        // 关闭当前实例
        Current.Shutdown();
        return null!;
    }

    /// <summary>
    ///     注册服务
    /// </summary>
    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册视图和ViewModel
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();

        // 注册公共服务
        containerRegistry.AddCommonServices();
        containerRegistry.AddShardUi();
        containerRegistry.AddPhotoCamera();
        containerRegistry.AddLicenseService(); // 添加授权服务

        containerRegistry.RegisterSingleton<HttpClient>();

        // 注册聚水潭服务
        containerRegistry.RegisterSingleton<IJuShuiTanService, JuShuiTanService>();

        // 注册包裹中转服务
        containerRegistry.RegisterSingleton<PackageTransferService>();
        containerRegistry.RegisterForNavigation<BalanceSortSettingsView, BalanceSortSettingsViewModel>();
        containerRegistry.RegisterForNavigation<BarcodeChuteSettingsView, BarcodeChuteSettingsViewModel>();
        containerRegistry.RegisterDialog<SettingsDialog, SettingsDialogViewModel>("SettingsDialog");

        // 注册聚水潭设置页面
        containerRegistry.RegisterForNavigation<JushuitanSettingsPage, JushuitanSettingsViewModel>();

        // 获取设置服务
        var settingsService = Container.Resolve<ISettingsService>();

        // 注册多摆轮分拣服务
        containerRegistry.RegisterPendulumSortService(settingsService, PendulumServiceType.Multi);
        containerRegistry.RegisterSingleton<IHostedService, PendulumSortHostedService>();
    }

    /// <summary>
    ///     启动
    /// </summary>
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

        Log.Information("应用程序启动");
        base.OnStartup(e);

        try
        {
            // 验证授权
            if (!CheckLicense())
            {
                Current.Shutdown();
                return;
            }

            // 启动相机托管服务
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            cameraStartupService.StartAsync(CancellationToken.None).Wait();
            Log.Information("相机托管服务启动成功");

            // 启动摆轮分拣托管服务
            var pendulumHostedService = Container.Resolve<PendulumSortHostedService>();
            pendulumHostedService.StartAsync(CancellationToken.None).Wait();
            Log.Information("摆轮分拣托管服务启动成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动托管服务时发生错误");
            throw;
        }
    }

    /// <summary>
    ///     验证授权
    /// </summary>
    /// <returns>验证是否通过</returns>
    private bool CheckLicense()
    {
        try
        {
            var licenseService = Container.Resolve<ILicenseService>();
            var (isValid, message) = licenseService.ValidateLicenseAsync().Result;

            if (!isValid)
            {
                Log.Warning("授权验证失败: {Message}", message);
                MessageBox.Show(message ?? "软件授权验证失败，请联系厂家获取授权。", "授权验证", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // 获取授权过期时间并计算剩余天数
            var expirationDate = licenseService.GetExpirationDateAsync().Result;
            var daysLeft = (expirationDate - DateTime.Now).TotalDays;
            Log.Information("授权剩余天数: {DaysLeft} 天", Math.Ceiling(daysLeft));

            if (!string.IsNullOrEmpty(message))
            {
                // 有效但有警告消息（如即将过期）
                Log.Warning("授权警告: {Message}", message);
                MessageBox.Show(message, "授权提醒", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                Log.Information("授权验证通过");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "授权验证过程发生错误");
            MessageBox.Show("授权验证过程发生错误，请联系厂家获取支持。", "授权验证", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    ///     退出
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Information("应用程序开始关闭...");
            // 停止托管服务
            try
            {
                Log.Information("正在停止托管服务...");

                // 停止摆轮分拣托管服务
                var pendulumHostedService = Container.Resolve<IHostedService>();
                pendulumHostedService.StopAsync(CancellationToken.None).Wait();
                Log.Information("摆轮分拣托管服务已停止");

                // 停止相机托管服务
                var cameraStartupService = Container.Resolve<CameraStartupService>();
                cameraStartupService.StopAsync(CancellationToken.None).Wait();
                Log.Information("相机托管服务已停止");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "停止托管服务时发生错误");
            }

            // 释放资源
            try
            {
                // 释放相机工厂
                var cameraFactory = Container.Resolve<CameraFactory>();
                cameraFactory.Dispose();
                Log.Information("相机工厂已释放");

                // 释放相机服务
                var cameraService = Container.Resolve<ICameraService>();
                cameraService.Dispose();
                Log.Information("相机服务已释放");

                // 释放摆轮分拣服务
                if (Container.Resolve<IPendulumSortService>() is IDisposable pendulumService)
                {
                    pendulumService.Dispose();
                    Log.Information("摆轮分拣服务已释放");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放资源时发生错误");
            }

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
            // 释放 Mutex
            _mutex?.Dispose();
            _mutex = null;
            base.OnExit(e);
        }
    }
}
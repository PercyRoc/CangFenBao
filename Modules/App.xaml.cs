using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using Common.Extensions;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using DeviceService.Extensions;
using Serilog;
using ShanghaiModuleBelt.Data;
using ShanghaiModuleBelt.Services;
using ShanghaiModuleBelt.Services.Jitu;
using ShanghaiModuleBelt.Services.Sto;
using ShanghaiModuleBelt.Services.Yunda;
using ShanghaiModuleBelt.Services.Zto;
using ShanghaiModuleBelt.ViewModels;
using ShanghaiModuleBelt.ViewModels.Jitu.Settings;
using ShanghaiModuleBelt.ViewModels.Settings;
using ShanghaiModuleBelt.ViewModels.Sto.Settings;
using ShanghaiModuleBelt.ViewModels.Yunda.Settings;
using ShanghaiModuleBelt.ViewModels.Zto.Settings;
using ShanghaiModuleBelt.Views;
using ShanghaiModuleBelt.Views.Jitu.Settings;
using ShanghaiModuleBelt.Views.Settings;
using ShanghaiModuleBelt.Views.Sto.Settings;
using ShanghaiModuleBelt.Views.Yunda.Settings;
using ShanghaiModuleBelt.Views.Zto.Settings;
using SharedUI.Extensions;
using SharedUI.ViewModels.Settings;
using SharedUI.Views.Settings;

namespace ShanghaiModuleBelt;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
public partial class App
{
    private const string MutexName = "Global\\Modules_App_Mutex";
    private static Mutex? _mutex;
    private bool _ownsMutex;

    /// <summary>
    ///     创建主窗口
    /// </summary>
    protected override Window CreateShell()
    {
        // 检查是否已经运行（进程级检查）
        if (IsApplicationAlreadyRunning())
        {
            MessageBox.Show("程序已在运行中，请勿重复启动！", "提示", MessageBoxButton.OK,
                MessageBoxImage.Information);
            Environment.Exit(0); // 直接退出进程
            return null!;
        }

        try
        {
            // 尝试创建全局Mutex
            _mutex = new Mutex(true, MutexName, out var createdNew);
            _ownsMutex = createdNew;

            if (createdNew)
            {
                return Container.Resolve<MainWindow>();
            }

            // 尝试获取已存在的Mutex，如果无法获取，说明有一个正在运行的实例
            var canAcquire = _mutex.WaitOne(TimeSpan.Zero, false);
            if (!canAcquire)
            {
                MessageBox.Show("程序已在运行中，请勿重复启动！", "提示", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Environment.Exit(0); // 直接退出进程
                return null!; // 虽然不会执行到这里，但需要满足返回类型
            }
            // 可以获取Mutex，说明前一个实例可能异常退出但Mutex已被释放
            _ownsMutex = true;
            return Container.Resolve<MainWindow>();
        }
        catch (Exception ex)
        {
            // Mutex创建或获取失败
            Log.Error(ex, "检查应用程序实例时发生错误");
            MessageBox.Show($"启动程序时发生错误: {ex.Message}", "错误", MessageBoxButton.OK,
                MessageBoxImage.Error);
            Current.Shutdown();
            return null!;
        }
    }

    /// <summary>
    ///     检查是否已有相同名称的应用程序实例在运行
    /// </summary>
    private static bool IsApplicationAlreadyRunning()
    {
        var currentProcess = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName(currentProcess.ProcessName);

        // 当前进程也会被计入，所以如果数量大于1则说明有其他实例
        return processes.Length > 1;
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

        // 注册 SQLite 数据库上下文
        containerRegistry.RegisterSingleton<ApplicationDbContext>();

        // 注册重传服务
        containerRegistry.RegisterSingleton<RetryService>();

        containerRegistry.RegisterSingleton<HttpClient>();
        // 注册包裹中转服务
        containerRegistry.RegisterSingleton<PackageTransferService>();

        // 注册格口映射服务
        containerRegistry.RegisterSingleton<ChuteMappingService>();
        containerRegistry.RegisterSingleton<BarcodeChuteSettingsViewModel>();

        // 注册模组连接服务
        containerRegistry.RegisterSingleton<IModuleConnectionService, ModuleConnectionService>();
        containerRegistry.RegisterSingleton<ModuleConnectionHostedService>();
        containerRegistry.Register<ModuleConnectionHostedService>();

        // 注册格口包裹记录服务
        containerRegistry.RegisterSingleton<ChutePackageRecordService>();

        // 注册申通自动揽收服务
        containerRegistry.RegisterSingleton<IStoAutoReceiveService, StoAutoReceiveService>();

        // 注册韵达上传重量服务
        containerRegistry.RegisterSingleton<IYundaUploadWeightService, YundaUploadWeightService>();

        // 注册中通API服务
        containerRegistry.RegisterSingleton<IZtoApiService, ZtoApiService>();

        // 注册极兔API服务
        containerRegistry.RegisterSingleton<IJituService, JituService>();

        containerRegistry.RegisterForNavigation<ModuleConfigView, ModuleConfigViewModel>();
        // containerRegistry.RegisterForNavigation<TcpSettingsView, TcpSettingsViewModel>();
        containerRegistry.RegisterForNavigation<BarcodeChuteSettingsView, BarcodeChuteSettingsViewModel>();
        containerRegistry.RegisterForNavigation<StoApiSettingsView, StoApiSettingsViewModel>();
        containerRegistry.RegisterForNavigation<YundaApiSettingsView, YundaApiSettingsViewModel>();
        containerRegistry.RegisterForNavigation<ZtoApiSettingsView, ZtoApiSettingsViewModel>();

        // 注册极兔设置界面
        containerRegistry.RegisterForNavigation<JituSettingsView, JituSettingsViewModel>();

        // 注册设置窗口
        containerRegistry.RegisterDialog<SettingsDialog, SettingsDialogViewModel>("SettingsDialog");
    }

    /// <summary>
    ///     启动
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        // 配置Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.Debug()
            .WriteTo.File("logs/app-.log",
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true)
            .CreateLogger();

        Log.Information("应用程序启动");
        // 先调用基类方法初始化容器
        base.OnStartup(e);

        try
        {
            // 启动相机托管服务
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            _ = Task.Run(async () =>
            {
                await cameraStartupService.StartAsync(CancellationToken.None);
                Log.Information("相机托管服务启动成功");
            });

            // 启动模组连接托管服务
            var moduleConnectionHostedService = Container.Resolve<ModuleConnectionHostedService>();
            _ = Task.Run(async () =>
            {
                await moduleConnectionHostedService.StartAsync(CancellationToken.None);
                Log.Information("模组连接托管服务启动成功");
            });

            // // 启动锁格托管服务
            // var lockingHostedService = Container.Resolve<LockingHostedService>();
            // _ = Task.Run(async () =>
            // {
            //     await lockingHostedService.StartAsync();
            //     Log.Information("锁格托管服务启动成功");
            // });

            // 初始化锁格服务
            // _ = Container.Resolve<LockingService>();
            // Log.Information("锁格服务初始化成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动托管服务时发生错误");
            throw;
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

                // 停止相机托管服务
                var cameraStartupService = Container.Resolve<CameraStartupService>();
                Task.Run(async () => await cameraStartupService.StopAsync(CancellationToken.None)).Wait(2000);
                Log.Information("相机托管服务已停止");

                // 停止模组连接托管服务
                var moduleConnectionHostedService = Container.Resolve<ModuleConnectionHostedService>();
                Task.Run(async () => await moduleConnectionHostedService.StopAsync(CancellationToken.None)).Wait(2000);
                Log.Information("模组连接托管服务已停止");

                // 停止重传服务
                if (Container.Resolve<RetryService>() is IDisposable retryService)
                {
                    retryService.Dispose();
                    Log.Information("重传服务已停止");
                }

                // 释放数据库上下文
                if (Container.Resolve<ApplicationDbContext>() is IDisposable dbContext)
                {
                    dbContext.Dispose();
                    Log.Information("数据库上下文已释放");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "停止托管服务时发生错误");
            }

            // 释放资源
            try
            {
                // 释放相机工厂
                if (Container.Resolve<CameraFactory>() is IDisposable cameraFactory)
                {
                    cameraFactory.Dispose();
                    Log.Information("相机工厂已释放");
                }

                // 释放相机服务
                if (Container.Resolve<ICameraService>() is IDisposable cameraService)
                {
                    cameraService.Dispose();
                    Log.Information("相机服务已释放");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放资源时发生错误");
            }

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
            try
            {
                // 安全释放 Mutex
                if (_mutex != null)
                {
                    if (_ownsMutex && _mutex.SafeWaitHandle is { IsClosed: false, IsInvalid: false })
                    {
                        _mutex.ReleaseMutex();
                        Log.Information("Mutex已释放");
                    }

                    _mutex.Dispose();
                    _mutex = null;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放Mutex时发生错误");
            }

            base.OnExit(e);
        }
    }
}
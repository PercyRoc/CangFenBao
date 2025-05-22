using System.Windows;
using BalanceSorting.Modules;
using BalanceSorting.Service;
using Camera;
using Camera.Services.Implementations.TCP;
using Common;
using Common.Services.Settings;
using Common.Services.Ui;
using History;
using History.Data;
using JinHuaQiHang.ViewModels;
using JinHuaQiHang.Views;
using Microsoft.Extensions.Configuration;
using Serilog;
using Server.JuShuiTan;
using SharedUI.ViewModels.Settings;
using SharedUI.Views.Windows;

namespace JinHuaQiHang;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App 
{
    private static Mutex? _mutex;
    private const string MutexName = @"Global\JinHuaQiHang_App_Mutex";
    private bool _ownsMutex;

    protected override Window CreateShell()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out var createdNew);
            _ownsMutex = createdNew;

            if (createdNew || _mutex.WaitOne(TimeSpan.Zero, false))
            {
                _ownsMutex = true;
                var moduleManager = Container.Resolve<IModuleManager>();
                moduleManager.Run(); 
                
                return Container.Resolve<MainWindow>();
            }

            MessageBox.Show("应用已运行", "Information", MessageBoxButton.OK,
                MessageBoxImage.Information);
            Environment.Exit(0);
            return null!;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "创建 Shell 期间发生致命错误。");
            MessageBox.Show($"启动期间发生致命错误: {ex.Message}", "启动错误", MessageBoxButton.OK,
                MessageBoxImage.Error);
            Environment.Exit(1);
            return null!;
        }
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterInstance(Log.Logger);

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
        containerRegistry.RegisterInstance<IConfiguration>(configuration);
        containerRegistry.RegisterSingleton<ISettingsService, SettingsService>();
        containerRegistry.RegisterSingleton<INotificationService,NotificationService>();
        containerRegistry.RegisterSingleton<BarcodeChuteSettingsViewModel>();
        
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();
        containerRegistry.RegisterDialogWindow<HistoryDialogWindow>();
        containerRegistry.RegisterDialog<SettingsDialogs, SettingsDialogViewModel>();
    }


    protected override void ConfigureModuleCatalog(IModuleCatalog moduleCatalog)
    {
        base.ConfigureModuleCatalog(moduleCatalog);
        moduleCatalog.AddModule<CommonServicesModule>();
        moduleCatalog.AddModule<TcpCameraModule>();
        moduleCatalog.AddModule<MultiPendulumSortModule>();
        moduleCatalog.AddModule<HistoryModule>();
        moduleCatalog.AddModule<JuShuiTanModule>();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        ConfigureLoggerFromSettings();
        RegisterGlobalExceptionHandling();

        // 单实例互斥体
        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            Log.Warning("已有实例在运行，程序即将退出。");
            MessageBox.Show("智利播种墙已在运行。", "已启动", MessageBoxButton.OK, MessageBoxImage.Information);
            Environment.Exit(0);
        }

        // 关键：确保模块先初始化
        var moduleManager = Container.Resolve<IModuleManager>();
        moduleManager.Run();

        base.OnStartup(e);
    }

    protected override async void OnInitialized()
    {
        base.OnInitialized();
        try
        {
            var historyService = Container.Resolve<IPackageHistoryDataService>();
            await historyService.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化 IPackageHistoryDataService 期间发生错误.");
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("应用程序退出 (OnExit). 退出代码: {ExitCode}", e.ApplicationExitCode);
        
        try
        {
            TryDisposeService<TcpCameraService>("TcpCameraService");
            TryDisposeService<IPendulumSortService>("MultiPendulumSortService");
            
            Log.Information("应用程序服务关闭完成或已发起。");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
            Log.Warning("服务关闭因超时而被取消 (OperationCanceledException).");
        }
        catch (AggregateException ae) when (ae.InnerExceptions.Any(ex => ex is OperationCanceledException))
        {
            Log.Warning("服务关闭因超时而被取消 (AggregateException).");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "等待服务关闭期间出错.");
        }
        finally
        {
            await Log.CloseAndFlushAsync();
            ReleaseMutex();
            base.OnExit(e);
        }
    }

    private static void ConfigureLoggerFromSettings()
    {
        try
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();
            Log.Information("已从 appsettings.json 配置 Serilog (called from ConfigureLoggerFromSettings)");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "从 appsettings.json 配置 Serilog 失败. 使用现有或基本备用日志记录器.");
        }
    }

    private void RegisterGlobalExceptionHandling()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "捕获到未处理的调度程序异常");
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Fatal(args.Exception, "捕获到未处理的任务异常");
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "捕获到未处理的 AppDomain 异常. IsTerminating: {IsTerminating}",
                args.IsTerminating);
        };
        Log.Information("全局异常处理程序已注册");
    }

    private void TryDisposeService<TService>(string serviceName) where TService : class, IDisposable
    {
        try
        {
            var service = Container.Resolve<TService>();
            if (service is IPendulumSortService pendulumSortService)
            {
                pendulumSortService.StopAsync();
            }
            service.Dispose();
        }
        catch (Exception ex) 
        {
            Log.Warning(ex, "解析或处置服务 {ServiceName} 时发生错误。该服务可能未注册、已被处置或在处置过程中出错。", serviceName);
        }
    }

    private void ReleaseMutex()
    {
        try
        {
            if (_mutex == null) return;
            if (_ownsMutex && _mutex.SafeWaitHandle is { IsClosed: false, IsInvalid: false })
            {
                try
                {
                    _mutex.ReleaseMutex();
                    Log.Information("Mutex 已释放");
                }
                catch (ApplicationException ex)
                {
                    Log.Warning(ex, "释放 Mutex 失败 (已释放?)");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "释放 Mutex 出错");
                }
            }

            _mutex.Dispose();
            _mutex = null;
            _ownsMutex = false;
            Log.Debug("Mutex 已处置");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "在 Mutex 释放/处置期间出错");
        }
    }
}
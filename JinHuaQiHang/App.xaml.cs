using System.Windows;
using BalanceSorting.Modules;
using BalanceSorting.Service;
using Camera;
using Camera.Services.Implementations.TCP;
using Common;
using Common.Services.Ui;
using History;
using JinHuaQiHang.ViewModels;
using JinHuaQiHang.Views;
using Microsoft.Extensions.Configuration;
using Serilog;
using Server.JuShuiTan;

namespace JinHuaQiHang;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : PrismApplication
{
    private static Mutex? _mutex;
    private const string MutexName = @"Global\JinHuaQiHang_App_Mutex";
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        ConfigureLoggerFromSettings();
        RegisterGlobalExceptionHandling();

        _mutex = new Mutex(true, MutexName, out var createdNew);
        _ownsMutex = createdNew;

        if (!_ownsMutex)
        {
            Log.Warning("[App] 检测到重复实例，应用程序将退出。");
            MessageBox.Show("程序已在运行，请勿重复启动！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            Current.Shutdown();
            return;
        }
        
        base.OnStartup(e);
    }

    protected override Window CreateShell()
    {
        try
        {
            var moduleManager = Container.Resolve<IModuleManager>();
            if (moduleManager != null)
            {
                moduleManager.Run();
            }
            else
            {
                Log.Warning("[App] CreateShell: IModuleManager was null, cannot explicitly run modules. This might be an issue.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[App] CreateShell: Error during explicit moduleManager.Run().");
        }

        try
        {
            var shell = Container.Resolve<MainWindow>();
            return shell;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[App] CreateShell 失败 (解析 MainWindow 时出错).");
            try
            {
                MessageBox.Show($"创建主窗口失败: {ex.Message}\n请检查日志获取详细信息。", "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // 如果连消息框都显示不了，只能记录日志然后退出
            }
            Current.Shutdown(-1);
            return null!;
        }
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        try
        {
            containerRegistry.RegisterInstance(Log.Logger);

            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();
            containerRegistry.RegisterInstance<IConfiguration>(configuration);
            
            containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();
            containerRegistry.RegisterDialog<SettingsDialogs, SettingsDialogViewModel>();
            containerRegistry.RegisterSingleton<INotificationService, NotificationService>();
            containerRegistry.RegisterDialog<History.Views.Dialogs.PackageHistoryDialogView, History.ViewModels.Dialogs.PackageHistoryDialogViewModel>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[App] RegisterTypes (App) 期间发生错误.");
            throw;
        }
    }

    protected override void ConfigureModuleCatalog(IModuleCatalog moduleCatalog)
    {
        try
        {
            base.ConfigureModuleCatalog(moduleCatalog);
            moduleCatalog.AddModule<CommonServicesModule>();
            moduleCatalog.AddModule<TcpCameraModule>();
            moduleCatalog.AddModule<MultiPendulumSortModule>();
            moduleCatalog.AddModule<HistoryModule>();
            moduleCatalog.AddModule<JuShuiTanModule>();
            moduleCatalog.AddModule<JinHuaQiHangModule>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[App] ConfigureModuleCatalog 期间发生错误.");
            throw;
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
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处置服务或记录日志期间出错 (OnExit).");
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
            Log.Logger = new LoggerConfiguration().WriteTo.Debug().WriteTo.File("logs/fallback-log-.txt", rollingInterval: RollingInterval.Day).CreateLogger();
            Log.Error(ex, "从 appsettings.json 配置 Serilog 失败. 使用备用日志记录器.");
        }
    }

    private void RegisterGlobalExceptionHandling()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "捕获到未处理的调度程序异常");
            MessageBox.Show($"发生严重错误，应用即将关闭: {args.Exception.Message}", "未处理异常", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Current.Shutdown(-1);
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
            if (args.IsTerminating)
            {
                MessageBox.Show($"发生致命的非UI线程错误，应用即将关闭: {(args.ExceptionObject as Exception)?.Message}", "未处理的AppDomain异常", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        Log.Information("全局异常处理程序已注册");
    }

    private void TryDisposeService<TService>(string serviceName) where TService : class
    {
        try
        {
            if (!Container.IsRegistered<TService>()) 
            {
                Log.Debug("服务 {ServiceName} 未注册，跳过处置。", serviceName);
                return;
            }

            var service = Container.Resolve<TService>();
            
            if (service is IPendulumSortService pendulumSortService)
            {
                Task.Run(pendulumSortService.StopAsync).Wait(TimeSpan.FromSeconds(5));
            }
            
            if (service is IAsyncDisposable asyncDisposableService)
            {
                ValueTask vt = asyncDisposableService.DisposeAsync();
                if (!vt.IsCompletedSuccessfully)
                {
                    vt.AsTask().Wait(TimeSpan.FromSeconds(5));
                }
            }
            else if (service is IDisposable disposableService)
            {
                disposableService.Dispose();
            }
            Log.Information("服务 {ServiceName} 已成功处置。", serviceName);
        }
        catch (Exception ex) 
        {
            Log.Warning(ex, "解析或处置服务 {ServiceName} 时发生错误。该服务可能已被处置或在处置过程中出错。", serviceName);
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
                    Log.Warning(ex, "释放 Mutex 失败 (当前线程可能不拥有它，或已释放)。");
                }
                catch (ObjectDisposedException ex)
                {
                    Log.Warning(ex, "释放 Mutex 失败 (Mutex 可能已被处置)。");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "释放 Mutex 时发生未知错误。");
                }
            }
            _mutex.Close();
            _mutex.Dispose();
            _mutex = null;
            _ownsMutex = false;
            Log.Debug("Mutex 已处置并标记。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "在 Mutex 释放/处置期间出错");
        }
    }
}
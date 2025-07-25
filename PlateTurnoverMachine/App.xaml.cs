﻿using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Common.Extensions;
using WPFLocalizeExtension.Engine;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using DeviceService.Extensions;
using DongtaiFlippingBoardMachine.Models;
using DongtaiFlippingBoardMachine.Services;
using DongtaiFlippingBoardMachine.ViewModels;
using DongtaiFlippingBoardMachine.ViewModels.Settings;
using DongtaiFlippingBoardMachine.Views;
using DongtaiFlippingBoardMachine.Views.Settings;
using Microsoft.Extensions.Hosting;
using Serilog;
using SharedUI.Extensions;
using SharedUI.ViewModels;
using SharedUI.Views.Dialogs;

namespace DongtaiFlippingBoardMachine;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
internal partial class App
{
    private const string MutexName = "Global\\PlateTurnoverMachine_App_Mutex";
    private static Mutex? _mutex;

    protected override Window CreateShell()
    {
        // 检查是否已经运行
        _mutex = new Mutex(true, MutexName, out var createdNew);

        if (createdNew) return Container.Resolve<MainWindow>();

        // 关闭当前实例
        Current.Shutdown();
        return null!;
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册视图和ViewModel
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();
        containerRegistry.RegisterForNavigation<PlateTurnoverSettingsView, PlateTurnoverSettingsViewModel>();

        // 注册公共服务
        containerRegistry.AddCommonServices();
        containerRegistry.AddShardUi();
        containerRegistry.AddPhotoCamera();

        // 注册 DWS 服务
        containerRegistry.RegisterSingleton<HttpClient>();

        // 注册包裹中转服务
        containerRegistry.RegisterSingleton<PackageTransferService>();

        // 注册设置窗口
        containerRegistry.RegisterDialog<SettingsDialog, SettingsDialogViewModel>("SettingsDialog");
        
        // 注册历史记录对话框
        containerRegistry.RegisterDialog<HistoryDialogView, HistoryDialogViewModel>("HistoryDialog");

        // 注册TCP连接服务
        containerRegistry.RegisterSingleton<ITcpConnectionService, TcpConnectionService>();

        // 注册中通分拣服务
        containerRegistry.RegisterSingleton<IZtoSortingService, ZtoSortingService>();

        containerRegistry.RegisterSingleton<SortingService>();
        containerRegistry.RegisterSingleton<PlateTurnoverSettings>();
        containerRegistry.RegisterSingleton<TcpConnectionHostedService>();
        containerRegistry.Register<IHostedService>(static sp => sp.Resolve<TcpConnectionHostedService>());
    }

    /// <summary>
    ///     启动
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        // 设置本地化为中文
        var culture = new CultureInfo("zh-CN");
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        
        // 初始化 WPFLocalizeExtension
        LocalizeDictionary.Instance.Culture = culture;
        LocalizeDictionary.Instance.SetCurrentThreadCulture = true;
        
        // 配置Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.Debug()
            .WriteTo.File("logs/app-.log",
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 10 * 1024 * 1024, // Limit file size to 10MB
                retainedFileCountLimit: 31) // Retain logs for 30 days (31 files total)
            .CreateLogger();

        // 注册全局异常处理
        RegisterGlobalExceptionHandling();

        Log.Information("应用程序启动");
        // 先调用基类方法初始化容器
        base.OnStartup(e);

        // 启动异步初始化
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            // 启动托管服务
            var hostedService = Container.Resolve<TcpConnectionHostedService>();
            await hostedService.StartAsync(CancellationToken.None);

            var cameraStartupService = Container.Resolve<CameraStartupService>();
            await cameraStartupService.StartAsync(CancellationToken.None);

            // 调用中通上线接口
            var ztoSortingService = Container.Resolve<IZtoSortingService>();
            var settings = Container.Resolve<ISettingsService>().LoadSettings<PlateTurnoverSettings>();

            // 初始化中通分拣服务配置
            ztoSortingService.Configure(settings.ZtoApiUrl, settings.ZtoCompanyId, settings.ZtoSecretKey);

            if (!string.IsNullOrEmpty(settings.ZtoPipelineCode))
            {
                Log.Information("正在调用中通上线接口...");
                var response = await ztoSortingService.ReportPipelineStatusAsync(settings.ZtoPipelineCode, "start");
                if (response?.Status == true)
                {
                    Log.Information("中通上线接口调用成功");
                }
                else
                {
                    Log.Warning("中通上线接口调用失败: {Message}", response?.Message);
                }
            }
            else
            {
                Log.Warning("未配置分拣线编码，无法调用中通上线接口");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动服务时发生错误");
            // 这里可以添加其他错误处理逻辑，比如显示错误对话框
            MessageBox.Show($"启动服务时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    ///     执行异步关闭操作
    /// </summary>
    public async Task PerformShutdownAsync()
    {
        try
        {
            Log.Information("开始执行异步关闭操作...");

            // 0. 新增：复位所有格口
            try
            {
                var sortingService = Container.Resolve<SortingService>();
                await sortingService.ResetAllChutesAsync();
            }
            catch (Exception resetEx)
            {
                Log.Error(resetEx, "复位所有格口时发生错误");
            }

            // 1. 先调用中通下线接口
            var ztoSortingService = Container.Resolve<IZtoSortingService>();
            var settings = Container.Resolve<ISettingsService>().LoadSettings<PlateTurnoverSettings>();

            if (!string.IsNullOrEmpty(settings.ZtoPipelineCode))
            {
                Log.Information("正在调用中通下线接口...");
                try
                {
                    var response = await ztoSortingService.ReportPipelineStatusAsync(settings.ZtoPipelineCode, "stop");
                    if (response?.Status == true)
                    {
                        Log.Information("中通下线接口调用成功");
                    }
                    else
                    {
                        Log.Warning("中通下线接口调用失败: {Message}", response?.Message);
                    }
                }
                catch (Exception downEx)
                {
                    Log.Error(downEx, "调用中通下线接口时发生错误");
                }
            }
            else
            {
                Log.Warning("未配置分拣线编码，无法调用中通下线接口");
            }

            // 2. 停止托管服务
            try
            {
                var hostedService = Container.Resolve<TcpConnectionHostedService>();
                await hostedService.StopAsync(CancellationToken.None);
            }
            catch (Exception svcEx)
            {
                Log.Error(svcEx, "停止TCP连接托管服务时发生错误");
            }

            try
            {
                var cameraStartupService = Container.Resolve<CameraStartupService>();
                await cameraStartupService.StopAsync(CancellationToken.None);
            }
            catch (Exception camEx)
            {
                Log.Error(camEx, "停止相机服务时发生错误");
            }

            // 等待所有日志写入完成
            Log.Information("异步关闭操作完成，正在刷新日志...");
            await Log.CloseAndFlushAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行异步关闭操作时发生错误");
            try
            {
                await Log.CloseAndFlushAsync(); // 尝试刷新日志
            }
            catch (Exception logEx)
            {
                Console.WriteLine(@$"刷新日志时也发生错误: {logEx}");
            }
        }
    }

    /// <summary>
    ///     退出
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        // OnExit 主要用于非常快速、同步的最终清理
        Log.Information("应用程序退出事件触发");
        try
        {
            // 注意：避免在此处执行复杂的或异步的清理逻辑
        }
        finally
        {
            // 释放 Mutex
            _mutex?.Dispose();
            _mutex = null;
            Log.Information("Mutex 已释放");
            base.OnExit(e);
        }
    }

    /// <summary>
    ///     注册全局异常处理程序
    /// </summary>
    private void RegisterGlobalExceptionHandling()
    {
        // UI线程未处理异常
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 非UI线程未处理异常
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // Task线程内未处理异常
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "UI线程发生未经处理的异常 (Dispatcher Unhandled Exception)");
        // 显示一个友好的对话框
        MessageBox.Show($"发生了一个无法恢复的严重错误，应用程序即将退出。\n\n错误信息：{e.Exception.Message}",
            "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);

        e.Handled = true; // 标记为已处理，防止默认的崩溃对话框

        // 尝试优雅地关闭
        _ = PerformShutdownAsync().ContinueWith(_ =>
        {
            Current.Dispatcher.Invoke(Current.Shutdown);
        });
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        Log.Fatal(exception, "非UI线程发生未经处理的异常 (AppDomain Unhandled Exception). IsTerminating: {IsTerminating}", e.IsTerminating);

        // 如果应用程序即将终止，确保日志被写入
        if (e.IsTerminating)
        {
            Log.CloseAndFlush();
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "一个后台任务发生未经观察的异常 (Unobserved Task Exception)");
        e.SetObserved(); // 防止进程终止
    }
}
﻿using System.Diagnostics;
using System.Windows;
using Common.Extensions;
using Common.Models.Settings.Sort.PendulumSort;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.Extensions;
using Microsoft.Extensions.Hosting;
using Serilog;
using SharedUI.Extensions;
using SharedUI.ViewModels.Settings;
using SharedUI.Views.Settings;
using SortingServices.Pendulum;
using SortingServices.Pendulum.Extensions;
using XinJuLi.Services.ASN;
using XinJuLi.ViewModels;
using XinJuLi.Views;
using XinJuLi.Views.Settings;

// 添加 Timer 引用

namespace XinJuLi;

/// <summary>
///     Interaction logic for App.xaml
/// </summary>
public partial class App
{
    // 使用 Global\ 前缀创建系统级 Mutex，更可靠
    private const string MutexName = "Global\\ShanghaiFaxunLogistics_App_Mutex";
    private static Mutex? _mutex;
    private bool _ownsMutex; // 用于跟踪是否持有 Mutex

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
            // 启动相机托管服务
            var cameraStartupService = Container.Resolve<CameraStartupService>();
            cameraStartupService.StartAsync(CancellationToken.None).Wait();
            Log.Information("相机托管服务启动成功");

            // // 启动摆轮分拣托管服务
            // var pendulumHostedService = Container.Resolve<PendulumSortHostedService>();
            // pendulumHostedService.StartAsync(CancellationToken.None).Wait();
            // Log.Information("摆轮分拣托管服务启动成功");

            // 启动ASN HTTP服务
            var asnHttpServer = Container.Resolve<AsnHttpServer>();
            asnHttpServer.StartAsync(CancellationToken.None).Wait();
            Log.Information("ASN HTTP服务启动成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动托管服务时发生错误");
            throw;
        }
    }

    protected override Window CreateShell()
    {
        // 检查是否已经运行（进程级检查）
        if (IsApplicationAlreadyRunning())
        {
            MessageBox.Show("程序已在运行中，请勿重复启动！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            Environment.Exit(0); // 直接退出进程
            return null!; // 虽然不会执行到这里，但需要满足返回类型
        }

        try
        {
            // 尝试创建全局Mutex
            _mutex = new Mutex(true, MutexName, out var createdNew);
            _ownsMutex = createdNew;

            if (createdNew)
            {
                Log.Information("成功获取Mutex，应用程序首次启动");
                return Container.Resolve<MainWindow>();
            }

            // 尝试获取已存在的Mutex，如果无法获取，说明有一个正在运行的实例
            Log.Warning("Mutex已存在，尝试获取...");
            var canAcquire = _mutex.WaitOne(TimeSpan.Zero, false);
            if (!canAcquire)
            {
                Log.Warning("无法获取现有Mutex，另一个实例正在运行");
                MessageBox.Show("程序已在运行中，请勿重复启动！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Environment.Exit(0); // 直接退出进程
                return null!;
            }
            // 可以获取Mutex，说明前一个实例可能异常退出但Mutex已被释放或从未正确获取
            Log.Warning("成功获取已存在的Mutex，可能是上一个实例异常退出");
            _ownsMutex = true; // 明确拥有权
            return Container.Resolve<MainWindow>();
        }
        catch (Exception ex)
        {
            // Mutex创建或获取失败
            Log.Error(ex, "检查应用程序实例时发生错误");
            MessageBox.Show($"启动程序时发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(1); // 使用非零代码表示错误退出
            return null!;
        }
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册视图和ViewModel
        containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>();
        containerRegistry.RegisterDialog<SettingsDialog, SettingsDialogViewModel>();
        // 新增对话框注册
        containerRegistry.RegisterDialog<AsnOrderConfirmDialog, AsnOrderConfirmDialogViewModel>("AsnOrderConfirmDialog");

        // 注册公共服务
        containerRegistry.AddCommonServices();
        containerRegistry.AddShardUi();
        containerRegistry.AddPhotoCamera();
        containerRegistry.RegisterForNavigation<BalanceSortSettingsView, BalanceSortSettingsViewModel>();
        containerRegistry.RegisterForNavigation<BarcodeChuteSettingsView, BarcodeChuteSettingsViewModel>();
        containerRegistry.RegisterPendulumSortService(PendulumServiceType.Multi);
        containerRegistry.RegisterSingleton<IHostedService, PendulumSortHostedService>();

        // 先注册MainWindowViewModel以便ASN服务可以引用
        containerRegistry.RegisterSingleton<MainWindowViewModel>();

        // 注册ASN服务和设置视图
        containerRegistry.RegisterSingleton<IAsnService, AsnService>();
        containerRegistry.RegisterSingleton<AsnHttpServer>();
        containerRegistry.RegisterForNavigation<AsnHttpSettingsView, AsnHttpSettingsViewModel>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Information("应用程序开始关闭...");

            // 停止托管服务
            try
            {
                Log.Information("正在停止托管服务...");

                // 停止ASN HTTP服务
                if (Container.Resolve<AsnHttpServer>() is IHostedService asnHttpServer)
                {
                    asnHttpServer.StopAsync(CancellationToken.None).Wait();
                    Log.Information("ASN HTTP服务已停止");
                }

                // 停止摆轮分拣托管服务
                var pendulumHostedService = Container.Resolve<PendulumSortHostedService>();
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
            try
            {
                // 安全释放 Mutex
                if (_mutex != null)
                {
                    // 只有拥有Mutex的实例才释放它
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

    /// <summary>
    ///     检查是否已有相同名称的应用程序实例在运行 (基于进程名)
    /// </summary>
    private static bool IsApplicationAlreadyRunning()
    {
        try
        {
            var currentProcess = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName(currentProcess.ProcessName);

            // 当前进程也会被计入，所以如果数量大于1则说明有其他实例
            if (processes.Length <= 1) return false;
            Log.Warning("检测到 {Count} 个同名进程正在运行", processes.Length);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "检查进程列表时发生错误");
            // 出错时保守处理，允许启动
            return false;
        }
    }
}
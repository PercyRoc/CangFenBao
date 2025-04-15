using System.Diagnostics; // 添加 Process 引用
using System.IO;
using System.Net.Http; // 添加 HttpClientFactory 引用
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection; // 添加 ServiceCollection 引用
using Prism.Ioc;
using Serilog;
using ShanghaiFaxunLogistics.Views;
using Timer = System.Timers.Timer; // 添加 Timer 引用

namespace ShanghaiFaxunLogistics
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App
    {
        private static Mutex? _mutex;

        // 使用 Global\ 前缀创建系统级 Mutex，更可靠
        private const string MutexName = "Global\\ShanghaiFaxunLogistics_App_Mutex";
        private Timer? _cleanupTimer;
        private bool _ownsMutex; // 用于跟踪是否持有 Mutex

        protected override void OnStartup(StartupEventArgs e)
        {
            // 1. 检查进程是否已运行 (早期检查)
            if (IsApplicationAlreadyRunning())
            {
                MessageBox.Show("应用程序已在运行中，请勿重复启动！ (进程检查)", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Environment.Exit(0); // 直接退出进程
                return;
            }

            // 2. 尝试获取 Mutex
            try
            {
                _mutex = new Mutex(true, MutexName, out var createdNew);
                _ownsMutex = createdNew;

                if (!createdNew)
                {
                    // 尝试获取已存在的Mutex，如果无法获取，说明有一个正在运行的实例
                    var canAcquire = _mutex.WaitOne(TimeSpan.Zero, false);
                    if (!canAcquire)
                    {
                        MessageBox.Show("应用程序已在运行中，请勿重复启动！ (Mutex检查)", "提示", MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        Environment.Exit(0); // 直接退出进程
                        return;
                    }

                    // 可以获取Mutex，说明前一个实例可能异常退出但Mutex已被释放
                    _ownsMutex = true;
                }
            }
            catch (Exception ex)
            {
                // Mutex创建或获取失败
                Log.Fatal(ex, "获取或创建 Mutex 时发生严重错误"); // 使用静态 Log
                MessageBox.Show($"检查应用程序实例时发生严重错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Current.Shutdown();
                return;
            }

            // 3. 配置 Logger (在 Mutex 之后，确保只有一个实例配置和使用日志文件)
            ConfigureLogger();
            Log.Information("应用程序启动，已获取 Mutex"); // 使用静态 Log

            // 4. 设置全局异常处理
            SetupGlobalExceptionHandling();

            // 5. 启动后台清理任务
            StartCleanupTask();

            // 6. 调用基类 OnStartup (会触发 CreateShell 和 RegisterTypes)
            base.OnStartup(e);

            // 7. 启动 IHostedService (如果使用 Generic Host，这会由 Host 完成)
            //    注意：在 PrismApplication 中没有内置的 Host，手动启动不是标准做法。
            //    考虑将服务逻辑放在非 HostedService 中，或引入 Generic Host。
            //    此处保留 BenFly 的逻辑作为示例，但需谨慎使用。
            // try
            // {
            //     // 示例：启动服务 (如果需要手动启动)
            //     // var myHostedService = Container.Resolve<MyHostedService>();
            //     // await myHostedService.StartAsync(CancellationToken.None);
            //     // _logger.Information("MyHostedService 启动成功");
            // }
            // catch (Exception ex)
            // {
            //     _logger.Error(ex, "启动托管服务时发生错误");
            //     // 根据严重性决定是否需要退出应用
            //     // MessageBox.Show("启动核心服务失败，应用程序将关闭。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            //     // Current.Shutdown();
            // }
        }

        protected override Window CreateShell()
        {
            // Mutex 检查已移至 OnStartup
            return Container.Resolve<MainWindow>();
        }

        protected override void RegisterTypes(IContainerRegistry containerRegistry)
        {
            // 注册配置
            containerRegistry.RegisterInstance(BuildConfiguration());

            // 注册 Serilog 日志记录器
            containerRegistry.RegisterInstance(Log.Logger);

            // 注册 HttpClientFactory
            var services = new ServiceCollection();
            // 配置 HttpClient (可以命名，可以设置 BaseAddress, Timeout 等)
            services.AddHttpClient("DefaultClient");
            var serviceProvider = services.BuildServiceProvider();
            containerRegistry.RegisterInstance(serviceProvider.GetRequiredService<IHttpClientFactory>());

            // 注册 Prism 服务 (例如 EventAggregator, DialogService 会自动注册)

            // 注册导航
            containerRegistry.RegisterForNavigation<MainWindow>(); // 注册主窗口本身，如果需要导航到它
            // containerRegistry.RegisterForNavigation<MainWindow, MainWindowViewModel>(); // 或者注册主窗口及其 ViewModel

            // 注册其他服务和 ViewModel
            // containerRegistry.RegisterSingleton<IMyDataService, MyDataService>();
            // containerRegistry.RegisterForNavigation<SomeView, SomeViewModel>();

            // 注册 IHostedService (如果需要，但注意 PrismApplication 不会自动管理它们的生命周期)
            // containerRegistry.RegisterSingleton<IHostedService, MyBackgroundService>();
        }

        // OnInitialized, ConfigureModuleCatalog 保持不变
        // ... existing code ...
        protected override void OnInitialized()
        {
            base.OnInitialized();

            // 主窗口显示后进行的操作，例如初始导航
            // var regionManager = Container.Resolve<IRegionManager>();
            // regionManager.RequestNavigate("ContentRegion", "MyView");

            // 订阅主窗口关闭事件以进行优雅关闭
            if (MainWindow != null)
            {
                MainWindow.Closing += MainWindow_Closing;
            }
            else
            {
                Log.Warning("MainWindow 为 null，无法订阅 Closing 事件进行优雅关闭"); // 使用静态 Log
            }
        }

        private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // 防止重复关闭
            if (MainWindow != null) MainWindow.Closing -= MainWindow_Closing;
            Log.Information("主窗口正在关闭，开始执行异步清理操作..."); // 使用静态 Log

            e.Cancel = true; // 阻止窗口立即关闭，以便异步操作完成

            try
            {
                // 在这里添加实际的异步关闭逻辑
                // 按照依赖关系逆序停止服务
                Log.Information("正在停止服务..."); // 使用静态 Log

                // 示例：停止假设的服务
                // var service1 = Container.Resolve<IService1>();
                // if (service1 is IAsyncDisposable asyncDisposable1) await asyncDisposable1.DisposeAsync();
                // else if (service1 is IDisposable disposable1) disposable1.Dispose();
                // else if (service1 is IStoppable stoppable1) await stoppable1.StopAsync();

                // var service2 = Container.Resolve<IService2>();
                // await service2.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token); // 带超时的停止

                // 等待一小段时间确保操作有机会完成 (可选, 最好依赖具体的完成信号)
                await Task.Delay(500);

                Log.Information("异步清理操作完成，准备退出应用程序。"); // 使用静态 Log
            }
            catch (OperationCanceledException) // 捕捉超时
            {
                Log.Warning("服务停止操作超时。"); // 使用静态 Log
            }
            catch (Exception ex)
            {
                Log.Error(ex, "关闭服务或执行异步清理时发生错误"); // 使用静态 Log
                // 可以选择显示错误信息给用户
                // MessageBox.Show("关闭应用程序时发生错误，部分数据可能未保存。", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                Log.Information("触发应用程序最终关闭流程。"); // 使用静态 Log
                // 允许应用程序关闭，这将最终调用 OnExit
                Current.Shutdown();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Information("应用程序 OnExit 事件触发"); // 使用静态 Log

            // 停止并清理后台任务定时器
            _cleanupTimer?.Stop();
            _cleanupTimer?.Dispose();
            Log.Information("清理任务定时器已停止并释放"); // 使用静态 Log

            // 同步释放资源 (如果还有未能在 MainWindow_Closing 中处理的)
            // 例如，某些服务可能只实现了 IDisposable
            try
            {
                // var syncService = Container.Resolve<ISyncService>();
                // syncService.Dispose();
                // _logger.Information("同步服务已释放");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "在 OnExit 中释放同步资源时出错"); // 使用静态 Log
            }

            // 安全释放 Mutex
            ReleaseMutex();

            // 关闭并刷新 Serilog (这是最后的操作之一)
            Log.Information("正在关闭并刷新日志..."); // 使用静态 Log
            Log.CloseAndFlush();

            base.OnExit(e);
            // This log might not be written depending on CloseAndFlush completion
            // Log.Information("应用程序完全退出");
        }

        // BuildConfiguration 保持不变
        // ... existing code ...
        private IConfiguration BuildConfiguration()
        {
            // 从 appsettings.json 及环境特定文件加载配置
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile(
                    $"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json",
                    optional: true)
                .AddEnvironmentVariables();

            return builder.Build();
        }

        // ConfigureLogger 保持不变 (可能需要更新 appsettings.json 配置)
        // ... existing code ...
        private void ConfigureLogger()
        {
            Container.Resolve<IConfiguration>();

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.Debug()
                .WriteTo.File("logs/app-.log",
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: true) // 示例: 每天滚动或达到大小限制时滚动
                .Enrich.FromLogContext() // 重新添加 Enrich
                .CreateLogger();
        }

        // SetupGlobalExceptionHandling 保持不变
        // ... existing code ...
        private void SetupGlobalExceptionHandling()
        {
            // UI 线程未处理异常
            DispatcherUnhandledException += (_, e) =>
            {
                Log.Error(e.Exception, "UI 线程发生未处理异常"); // 使用静态 Log
                MessageBox.Show($"发生未处理的UI异常: {e.Exception.Message}\n应用程序可能需要关闭。", "严重错误", MessageBoxButton.OK,
                    MessageBoxImage.Error);
                // 根据需要决定是否标记为已处理 e.Handled = true;
                // 或者让应用程序崩溃 Current.Shutdown();
            };

            // Task Scheduler 未处理异常 (后台线程)
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error(e.Exception, "Task Scheduler 捕获到未观察到的异常"); // 使用静态 Log
                // 对于后台异常，通常记录后设置 SetObserved()，避免进程崩溃
                e.SetObserved();
            };

            // AppDomain 未处理异常 (最终防线)
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                var exception = e.ExceptionObject as Exception;
                // 使用 Fatal 级别记录，因为这通常意味着应用程序即将终止
                Log.Fatal(exception, "AppDomain 捕获到未处理异常. IsTerminating: {IsTerminating}", e.IsTerminating); // 使用静态 Log
                // 这里通常无法阻止应用程序终止，只能尽量记录日志
                ReleaseMutex(); // 尝试在崩溃前释放 Mutex
                Log.CloseAndFlush(); // 尝试在崩溃前写入日志
            };
        }

        // --- 添加的方法 ---

        /// <summary>
        /// 检查是否已有相同名称的应用程序实例在运行 (基于进程名)
        /// </summary>
        private static bool IsApplicationAlreadyRunning()
        {
            try
            {
                var currentProcess = Process.GetCurrentProcess();
                var processes = Process.GetProcessesByName(currentProcess.ProcessName);
                // 当前进程也会被计入，所以如果数量大于1则说明有其他实例
                return processes.Length > 1;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "检查进程实例时发生错误"); // 使用静态 Log
                return false; // 出错时，保守地认为没有其他实例在运行
            }
        }

        /// <summary>
        /// 启动定期清理 DUMP 文件的任务
        /// </summary>
        private void StartCleanupTask()
        {
            try
            {
                // 每小时检查一次
                _cleanupTimer = new Timer(TimeSpan.FromHours(1).TotalMilliseconds);
                _cleanupTimer.Elapsed += (_, _) => // 使用 discard 代替 static
                {
                    try
                    {
                        CleanupDumpFiles();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "定时清理DUMP文件时发生错误"); // 使用静态 Log
                    }
                };
                _cleanupTimer.Start();
                Log.Information("DUMP 文件自动清理任务已启动，每小时执行一次"); // 使用静态 Log

                // 应用启动时立即执行一次清理
                Task.Run(() => // 使用实例方法，可以访问 _logger
                {
                    try
                    {
                        Log.Information("执行初始 DUMP 文件清理..."); // 使用静态 Log
                        CleanupDumpFiles();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "初始清理DUMP文件时发生错误"); // 使用静态 Log
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动 DUMP 文件清理任务时发生错误"); // 使用静态 Log
            }
        }

        /// <summary>
        /// 清理应用程序根目录下的 DUMP 文件 (*.dmp)
        /// </summary>
        private static void CleanupDumpFiles() // 改回 static
        {
            try
            {
                var dumpPath = AppDomain.CurrentDomain.BaseDirectory;
                var dumpFiles = Directory.GetFiles(dumpPath, "*.dmp", SearchOption.TopDirectoryOnly);

                if (dumpFiles.Length == 0)
                {
                    return; // 没有文件需要清理
                }

                Log.Information("发现 {Count} 个 DUMP 文件，尝试清理...", dumpFiles.Length); // 使用静态 Log
                var deletedCount = 0;
                var failedCount = 0;
                foreach (var file in dumpFiles)
                {
                    try
                    {
                        File.Delete(file);
                        deletedCount++;
                    }
                    catch (IOException ioEx)
                    {
                        // 文件可能被占用，记录警告
                        Log.Warning(ioEx, "清理 DUMP 文件失败 (可能被占用): {FilePath}", file); // 使用静态 Log
                        failedCount++;
                    }
                    catch (UnauthorizedAccessException uaEx)
                    {
                        Log.Warning(uaEx, "清理 DUMP 文件失败 (权限不足): {FilePath}", file); // 使用静态 Log
                        failedCount++;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "清理 DUMP 文件时发生未知错误: {FilePath}", file); // 使用静态 Log
                        failedCount++;
                    }
                }

                if (deletedCount > 0)
                {
                    Log.Information("成功清理 {DeletedCount} 个 DUMP 文件。", deletedCount); // 使用静态 Log
                }

                if (failedCount > 0)
                {
                    Log.Warning("{FailedCount} 个 DUMP 文件清理失败。", failedCount); // 使用静态 Log
                }
            }
            catch (Exception ex)
            {
                // 获取文件列表等操作也可能失败
                Log.Error(ex, "在查找或清理 DUMP 文件过程中发生错误"); // 使用静态 Log
            }
        }

        /// <summary>
        /// 安全地释放应用程序 Mutex
        /// </summary>
        private void ReleaseMutex()
        {
            try
            {
                if (_mutex == null) return;
                if (_ownsMutex)
                {
                    // 检查句柄是否仍然有效且未关闭
                    if (_mutex.SafeWaitHandle is { IsClosed: false, IsInvalid: false })
                    {
                        _mutex.ReleaseMutex();
                        Log.Information("应用程序 Mutex 已成功释放"); // 使用静态 Log (null check needed if logger isn't guaranteed)
                    }
                    else
                    {
                        Log.Warning("尝试释放 Mutex，但句柄已关闭或无效"); // 使用静态 Log
                    }

                    _ownsMutex = false; // 标记不再拥有
                }

                _mutex.Dispose();
                _mutex = null;
                Log.Information("Mutex 对象已 Dispose"); // 使用静态 Log
            }
            catch (ApplicationException appEx)
            {
                // 例如，尝试释放不属于当前线程的 Mutex 时
                Log.Error(appEx, "释放 Mutex 时发生 ApplicationException (可能已被其他线程释放或未持有)"); // 使用静态 Log
            }
            catch (ObjectDisposedException odEx)
            {
                Log.Warning(odEx, "尝试释放 Mutex 时发生 ObjectDisposedException (已被 Dispose)"); // 使用静态 Log
            }
            catch (Exception ex)
            {
                // 其他潜在异常
                Log.Error(ex, "释放 Mutex 时发生未知错误"); // 使用静态 Log
            }
            finally
            {
                _mutex = null; // 确保引用置空
                _ownsMutex = false;
            }
        }
    }
}
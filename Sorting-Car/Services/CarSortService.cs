using System.Collections.Concurrent;
using Common.Models.Package;
using Common.Services.Settings;
using Serilog;
using Sorting_Car.Models;
using Common.Services.Devices;

namespace Sorting_Car.Services;

/// <summary>
/// 小车分拣服务实现
/// </summary>
public class CarSortService : IDevice // 实现 IDevice，隐式实现 IAsyncDisposable
{
    private readonly ICarSortingDevice _carSortingService;
    private readonly ISettingsService _settingsService;
    private readonly ConcurrentQueue<PackageInfo> _sortingQueue = new();
    private readonly ConcurrentDictionary<int, PackageInfo> _processingPackages = new();
    private readonly SemaphoreSlim _processSemaphore;

    private readonly CancellationTokenSource _disposeCancellationTokenSource = new();

    private bool _isRunning;
    private Task? _processingTask;
    private CarSerialPortSettings? _serialPortSettings;
    private const int MaxConcurrentSorting = 5;

    /// <summary>
    /// 初始化小车分拣服务
    /// </summary>
    /// <param name="carSortingService">底层小车命令发送服务</param>
    /// <param name="settingsService">设置服务</param>
    public CarSortService(ICarSortingDevice carSortingService, ISettingsService settingsService)
    {
        _carSortingService = carSortingService ?? throw new ArgumentNullException(nameof(carSortingService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _processSemaphore = new SemaphoreSlim(MaxConcurrentSorting);
        Log.Information("CarSortService 创建成功，最大并行处理数: {MaxConcurrent}", MaxConcurrentSorting);

        // 订阅底层设备的连接状态变更事件
        _carSortingService.ConnectionChanged += OnUnderlyingConnectionChanged;
    }

    // 实现 IDevice.IsConnected
    public bool IsConnected => _carSortingService.IsConnected;

    // 实现 IDevice.ConnectionChanged (从底层设备转发)
    public event EventHandler<(string DeviceName, bool IsConnected)>? ConnectionChanged;

    private void OnUnderlyingConnectionChanged(object? sender, (string DeviceName, bool IsConnected) e)
    {
        var (deviceName, isConnected) = e;
        Log.Information("底层小车设备连接状态变更: {Status}", isConnected ? "已连接" : "已断开");
        // 转发事件
        ConnectionChanged?.Invoke(this, (deviceName, isConnected));
    }

    // 实现 IDevice.StartAsync (合并 InitializeAsync 逻辑)
    public async Task<bool> StartAsync()
    {
        if (_isRunning) // 检查 _isRunning 而不是 _isInitialized 以便公开 Start/Stop
        {
            Log.Information("CarSortService 已在运行中");
            return true;
        }

        if (_disposeCancellationTokenSource.IsCancellationRequested)
        {
             Log.Warning("CarSortService 已请求释放，无法启动。");
             return false; // 如果已请求释放则无法启动
        }

        Log.Information("CarSortService 启动开始...");

        try
        {
            // 1. 加载配置 (如果同步，则保留在此处或移至构造函数)
            // 看起来是同步的，所以保留在此处或构造函数中。
             _serialPortSettings = _settingsService.LoadSettings<CarSerialPortSettings>();
             if (_serialPortSettings == null)
             {
                 Log.Error("启动失败：无法加载串口设置 (CarSerialPortSettings)");
                 _isRunning = false; // 确保失败时状态为 false
                 return false;
             }

            // 启动底层设备
            Log.Information("正在启动底层小车设备...");
            var underlyingStarted = await _carSortingService.StartAsync();
            if (!underlyingStarted)
            {
                Log.Error("启动底层小车分拣设备失败");
                _isRunning = false;
                return false;
            }

            // 启动内部分拣处理任务
            if (_processingTask == null || _processingTask.IsCompleted)
            {
                _processingTask = Task.Run(() => ProcessSortingQueueAsync(_disposeCancellationTokenSource.Token),
                    _disposeCancellationTokenSource.Token);
                 Log.Information("分拣队列处理任务已启动");
            }

            _isRunning = true; // 设置运行状态
            Log.Information("CarSortService 启动成功");
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Information("CarSortService 启动操作被取消。");
            _isRunning = false;
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动 CarSortService 时发生异常");
            // 如果底层设备已启动，则尝试停止它
            try { _ = _carSortingService.StopAsync().ConfigureAwait(false).GetAwaiter().GetResult(); } catch { /* 忽略 */ }
            _isRunning = false;
            return false; // 表示失败
        }
    }

    // 实现 IDevice.StopAsync
    public async Task<bool> StopAsync()
    {
        if (!_isRunning) // 检查 _isRunning
        {
            Log.Information("CarSortService 未运行，无需停止。");
            return true; // 已停止
        }

        Log.Information("CarSortService 正在停止...");
        _isRunning = false; // 立即设置状态

        try
        {
            // 向内部分拣处理任务发送取消信号
            await _disposeCancellationTokenSource.CancelAsync();
            Log.Debug("已发出取消信号给分拣队列处理任务。");

            // 等待处理任务完成 (带超时)
            if (_processingTask is { IsCompleted: false })
            {
                Log.Debug("等待分拣队列处理任务完成...");
                try
                {
                    // 使用合理的关闭超时时间
                    await _processingTask.ConfigureAwait(false);
                    Log.Debug("分拣队列处理任务已完成。");
                }
                catch (OperationCanceledException) // 关闭期间预期发生
                {
                    Log.Debug("分拣队列处理任务因取消而终止。");
                }
                catch (Exception ex) // 记录任务等待期间的意外异常
                {
                    Log.Warning(ex, "等待分拣队列处理任务完成时发生异常。");
                }
                finally
                {
                    _processingTask = null; // 清除任务引用
                }
            }

            // 停止底层设备
            Log.Information("正在停止底层小车设备...");
            var underlyingStopped = await _carSortingService.StopAsync();
            if (!underlyingStopped)
            {
                Log.Warning("停止底层小车分拣设备失败。");
            }

            Log.Information("CarSortService 已停止。");
            return true; // 表示成功 (即使底层停止操作发出警告)
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止 CarSortService 时发生异常");
            return false; // 表示失败
        }
    }

    public Task<bool> ProcessPackageSortingAsync(PackageInfo package)
    {
        if (!_isRunning)
        {
            Log.Warning("CarSortService 未运行或已停止，无法处理包裹: {Barcode}", package.Barcode);
            return Task.FromResult(false);
        }

        if (package.ChuteNumber <= 0)
        {
            Log.Warning("包裹 {Barcode} 的格口号无效 ({ChuteNumber})，无法加入分拣队列",
                package.Barcode, package.ChuteNumber);
            return Task.FromResult(false);
        }

        try
        {
            // 添加到队列
            _sortingQueue.Enqueue(package);
            Log.Information("包裹 {Barcode} 已加入分拣队列，格口号: {ChuteNumber}",
                package.Barcode, package.ChuteNumber);

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "添加包裹到分拣队列时发生异常: {Barcode}", package.Barcode);
            return Task.FromResult(false);
        }
    }

    public Task<bool> ResetCarAsync()
    {
        if (!_isRunning)
        {
            Log.Warning("CarSortService 未运行，无法重置。");
            return Task.FromResult(false);
        }

        // 此方法实现重置小车的功能
        // 可以清空当前队列，停止所有处理
        Log.Information("请求重置小车分拣服务...");
        ClearQueuesAndProcessing();
        Log.Information("小车分拣服务已重置。");
        return Task.FromResult(true);
    }

    private void ClearQueuesAndProcessing()
    {
        // 清空队列
        int clearedQueueCount = 0;
        while (_sortingQueue.TryDequeue(out _))
        {
            clearedQueueCount++;
        }

        // 记录当前正在处理的包裹
        int clearedProcessingCount = _processingPackages.Count;
        _processingPackages.Clear();

        Log.Information("已清空分拣队列和处理中列表，清除了 {QueueCount} 个等待包裹和 {ProcessingCount} 个处理中包裹",
            clearedQueueCount, clearedProcessingCount);
    }

    /// <summary>
    /// 后台处理分拣队列
    /// </summary>
    private async Task ProcessSortingQueueAsync(CancellationToken cancellationToken)
    {
        Log.Information("开始处理分拣队列...");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // 检查队列是否为空
                if (_sortingQueue.IsEmpty)
                {
                    await Task.Delay(100, cancellationToken); // 短暂等待
                    continue;
                }

                // 尝试获取一个包裹
                if (!_sortingQueue.TryDequeue(out var package))
                    continue;

                // 等待信号量（控制并发数）
                await _processSemaphore.WaitAsync(cancellationToken);

                // 启动一个新任务处理该包裹
                // 将 cancellationToken 传递给内部任务，以便它们也能被取消
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (cancellationToken.IsCancellationRequested) return; // 提前退出
                        _processingPackages[package.Index] = package;
                        await ProcessSinglePackageAsync(package, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Debug("处理包裹 {Barcode} 的任务已取消。", package.Barcode);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "处理包裹时发生异常: {Barcode}", package.Barcode);
                    }
                    finally
                    {
                        _processingPackages.TryRemove(package.Index, out _);
                        _processSemaphore.Release();
                    }
                }, cancellationToken); // 传递 cancellationToken
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("分拣队列处理已按预期取消。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理分拣队列时发生致命异常");
        }

        Log.Information("分拣队列处理已停止。");
    }

    /// <summary>
    /// 处理单个包裹的分拣命令
    /// </summary>
    private async Task
        ProcessSinglePackageAsync(PackageInfo package, CancellationToken cancellationToken) // 添加 CancellationToken
    {
        try
        {
            // 检查串口设置
            if (_serialPortSettings == null)
            {
                Log.Error("未加载串口设置 (CarSerialPortSettings)，无法发送命令: {Barcode}", package.Barcode);
                return;
            }

            // 应用命令延迟
            var delayMs = _serialPortSettings.CommandDelayMs; // 假设 CarSerialPortSettings 有此属性
            if (delayMs > 0)
            {
                Log.Debug("延迟 {Delay}ms 后发送格口 {ChuteNumber} 的分拣命令",
                    delayMs, package.ChuteNumber);
                await Task.Delay(delayMs, cancellationToken); // 使用 cancellationToken
            }

            if (cancellationToken.IsCancellationRequested)
            {
                Log.Information("在发送命令前取消了包裹 {Barcode} 的处理。", package.Barcode);
                return;
            }

            // 发送分拣命令
            Log.Information("开始发送格口 {ChuteNumber} 的分拣命令，包裹: {Barcode}",
                package.ChuteNumber, package.Barcode);
            // 将 cancellationToken 传递给底层服务
            var result = _carSortingService.SendCommandForPackage(package, cancellationToken);

            if (result)
                Log.Information("包裹 {Barcode} 的分拣命令发送成功", package.Barcode);
            else
                Log.Warning("包裹 {Barcode} 的分拣命令发送失败", package.Barcode);
        }
        catch (OperationCanceledException)
        {
            Log.Information("处理包裹 {Barcode} 的分拣命令已按预期取消。", package.Barcode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹 {Barcode} 的分拣命令时发生异常", package.Barcode);
        }
    }

    // 实现 IAsyncDisposable.DisposeAsync (主要清理方法)
    public async ValueTask DisposeAsync()
    {
        // 清理逻辑已从 Dispose(bool disposing) 移至此处
        Log.Information("正在释放 CarSortService...");

        // 首先正常停止服务
        await StopAsync();

        // 释放内部 CTS
        _disposeCancellationTokenSource.Dispose();
        Log.Debug("CancellationTokenSource 已释放。");

        // 释放 SemaphoreSlim
        _processSemaphore.Dispose();
        Log.Debug("SemaphoreSlim 已释放。");

        // 底层设备的 DisposeAsync 由 StopAsync 调用，
        // 或者其生命周期由注入的 DI 容器管理。
        // 如果 CarSortService *拥有* 该设备，则会在此处调用 await device.DisposeAsync()。
        // 假设 DI 拥有它，我们不在这里释放它。

        // _isInitialized 和 _isRunning 在 StopAsync 后应为 false

        Log.Information("CarSortService 已释放。");

        // 由于我们使用 DisposeAsync 模式，因此禁止终结操作
        GC.SuppressFinalize(this);
    }
}
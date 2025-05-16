using System.Collections.Concurrent;
using Common.Models.Package;
using Common.Services.Settings;
using Serilog;
using Sorting_Car.Models;
using SortingServices.Car;

namespace Sorting_Car.Services;

/// <summary>
/// 小车分拣服务实现
/// </summary>
public class CarSortService : IDisposable
{
    private readonly CarSortingService _carSortingService;
    private readonly ISettingsService _settingsService;
    private readonly ConcurrentQueue<PackageInfo> _sortingQueue = new();
    private readonly ConcurrentDictionary<int, PackageInfo> _processingPackages = new();
    private readonly SemaphoreSlim _processSemaphore;
    
    private readonly CancellationTokenSource _disposeCancellationTokenSource = new(); 
    
    private bool _isRunning;
    private bool _isInitialized;
    private Task? _processingTask;
    private CarSerialPortSettings? _serialPortSettings; 
    private const int MaxConcurrentSorting = 5;
    private bool _disposedValue; // To detect redundant calls

    /// <summary>
    /// 初始化小车分拣服务
    /// </summary>
    /// <param name="carSortingService">底层小车命令发送服务</param>
    /// <param name="settingsService">设置服务</param>
    public CarSortService(CarSortingService carSortingService, ISettingsService settingsService)
    {
        _carSortingService = carSortingService ?? throw new ArgumentNullException(nameof(carSortingService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _processSemaphore = new SemaphoreSlim(MaxConcurrentSorting);
        Log.Information("CarSortService 创建成功，最大并行处理数: {MaxConcurrent}", MaxConcurrentSorting);
    }
    
    public bool IsConnected => _carSortingService.IsConnected;

    public bool IsRunning => _isRunning;
    
    public async Task<bool> InitializeAsync()
    {
        if (_isInitialized)
        {
            Log.Information("CarSortService 已初始化");
            return true;
        }
        
        try
        {
            // 加载配置
            // 确保使用正确的模型命名空间
            _serialPortSettings = _settingsService.LoadSettings<Models.CarSerialPortSettings>();
            if (_serialPortSettings == null)
            {
                Log.Error("初始化失败：无法加载串口设置 (CarSerialPortSettings)");
                return false;
            }
            
            // 初始化底层服务
            // 传递 _disposeCancellationTokenSource.Token 以便底层服务也可以响应全局的停止信号
            var result = _carSortingService.Initialize(_disposeCancellationTokenSource.Token);
            if (!result)
            {
                Log.Error("初始化底层小车分拣服务失败");
                return false;
            }
            
            _isInitialized = true;
            Log.Information("CarSortService 初始化成功");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化 CarSortService 时发生异常");
            return false;
        }
    }
    
    public Task<bool> StartAsync()
    {
        if (_disposedValue)
        {
            Log.Warning("CarSortService 已被释放，无法启动。");
            return Task.FromResult(false);
        }

        if (!_isInitialized)
        {
            Log.Error("分拣服务尚未初始化，无法启动");
            return Task.FromResult(false);
        }
        
        if (_isRunning)
        {
            Log.Information("分拣服务已在运行中");
            return Task.FromResult(true);
        }
        
        try
        {
            // 启动处理线程，使用 _disposeCancellationTokenSource.Token
            _processingTask = Task.Run(() => ProcessSortingQueueAsync(_disposeCancellationTokenSource.Token), _disposeCancellationTokenSource.Token);
            
            _isRunning = true;
            Log.Information("小车分拣服务已启动");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动小车分拣服务时发生异常");
            _isRunning = false; // 确保状态正确
            return Task.FromResult(false);
        }
    }

    // 将 StopAsync 变为私有，主要由 Dispose 调用
    private async Task<bool> StopInternalAsync() // Marked as unused, but it's good practice to have explicit stop if needed elsewhere.
    {
        if (!_isRunning)
        {
            Log.Information("分拣服务已停止或未运行。");
            return true;
        }
        
        Log.Information("正在停止小车分拣服务内部逻辑...");
        _isRunning = false; // 立即设置状态，防止新的请求进入处理逻辑

        // 触发取消信号，让 ProcessSortingQueueAsync 退出循环
        if (!_disposeCancellationTokenSource.IsCancellationRequested)
        {
            _disposeCancellationTokenSource.Cancel();
        }
        
        // 等待任务完成
        if (_processingTask != null)
        {
            try
            {
                Log.Debug("等待分拣队列处理任务完成...");
                // 使用带超时的等待，避免无限阻塞
                await Task.WhenAny(_processingTask, Task.Delay(TimeSpan.FromSeconds(5), _disposeCancellationTokenSource.IsCancellationRequested ? new CancellationToken(true) : CancellationToken.None)); 
                if (!_processingTask.IsCompleted)
                {
                    Log.Warning("分拣队列处理任务在超时时间内未完成。");
                }
                else
                {
                    Log.Information("分拣队列处理任务已完成。");
                }
            }
            catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is TaskCanceledException || e is OperationCanceledException))
            {
                Log.Information("分拣队列处理任务已按预期取消。");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "等待分拣队列处理任务完成时发生异常");
            }
            finally
            {
                _processingTask = null;
            }
        }
        
        // 清理队列和正在处理的包裹
        ClearQueuesAndProcessing();

        Log.Information("小车分拣服务内部逻辑已停止。");
        return true;
    }
    
    public Task<bool> ProcessPackageSortingAsync(PackageInfo package)
    {
         if (_disposedValue)
        {
            Log.Warning("CarSortService 已被释放，无法处理包裹: {Barcode}", package.Barcode);
            return Task.FromResult(false);
        }

        if (!_isInitialized || !_isRunning)
        {
            Log.Error("分拣服务未初始化或未运行，无法处理包裹: {Barcode}", package.Barcode);
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
        if (_disposedValue)
        {
            Log.Warning("CarSortService 已被释放，无法重置。");
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
                // 将cancellationToken传递给内部任务，以便它们也能被取消
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (cancellationToken.IsCancellationRequested) return; // 早退出
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
                }, cancellationToken); // 传递cancellationToken
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
    private async Task ProcessSinglePackageAsync(PackageInfo package, CancellationToken cancellationToken) // 添加CancellationToken
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
            var delayMs = _serialPortSettings.CommandDelayMs; // 假设CarSerialPortSettings有此属性
            if (delayMs > 0)
            {
                Log.Debug("延迟 {Delay}ms 后发送格口 {ChuteNumber} 的分拣命令", 
                    delayMs, package.ChuteNumber);
                await Task.Delay(delayMs, cancellationToken); // 使用cancellationToken
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
    
    protected virtual void Dispose(bool disposing)
    {
        if (_disposedValue) return;
        if (disposing)
        {
            Log.Information("正在释放 CarSortService...");
            // 停止服务并等待完成
            // StopInternalAsync 是异步的，但 Dispose 不是。需要处理这种情况。
            // 可以在这里调用 StopInternalAsync().Wait() 或 StopInternalAsync().GetAwaiter().GetResult()
            // 但这可能导致死锁，特别是在UI线程或有同步上下文的情况下。
            // 更好的方法是让 StopInternalAsync 尽可能快地发出取消信号，并让后台任务自行终止。
            // Dispose 主要负责发出停止信号和释放托管/非托管资源。
                
            if (!_disposeCancellationTokenSource.IsCancellationRequested)
            {
                _disposeCancellationTokenSource.Cancel(); // 发出取消信号
            }

            // 尝试等待任务完成，但不阻塞太久
            try
            {
                _processingTask?.Wait(TimeSpan.FromMilliseconds(500)); // 短暂等待
            }
            catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is TaskCanceledException || e is OperationCanceledException))
            {
                Log.Debug("在Dispose中，处理任务已取消。");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "在Dispose中等待处理任务完成时发生异常。");
            }
            finally
            {
                _processingTask = null; // 清理任务引用
            }
                
            _isRunning = false; // 确保状态更新

            // 释放 SemaphoreSlim
            _processSemaphore.Dispose();
            Log.Debug("SemaphoreSlim 已释放。");

            // 释放 CancellationTokenSource
            _disposeCancellationTokenSource.Dispose();
            Log.Debug("CancellationTokenSource 已释放。");

            // 底层服务的 DisposeAsync 应该由其自身的管理者（如DI容器）调用，
            // 或者如果 CarSortService 完全拥有 CarSortingService 实例且没有共享，则可以在这里调用。
            // 假设 CarSortingService 是注入的，不由 CarSortService 直接释放。
            // (_carSortingService as IDisposable)?.Dispose(); // 如果需要且它是 IDisposable
            // await _carSortingService.DisposeAsync(); // 如果是 IAsyncDisposable 并且这里可以异步

            Log.Information("CarSortService 已释放。");
        }
        _disposedValue = true;
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
} 
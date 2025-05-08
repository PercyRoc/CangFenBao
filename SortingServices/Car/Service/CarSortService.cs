using Common.Models.Package;
using Common.Services.Settings;

using Serilog;

using System.Collections.Concurrent;

namespace SortingServices.Car.Service;

/// <summary>
/// 小车分拣服务实现
/// </summary>
public class CarSortService 
{
    private readonly CarSortingService _carSortingService;
    private readonly ISettingsService _settingsService;
    private readonly ConcurrentQueue<PackageInfo> _sortingQueue = new();
    private readonly ConcurrentDictionary<int, PackageInfo> _processingPackages = new();
    private readonly SemaphoreSlim _processSemaphore;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    
    private bool _isRunning;
    private bool _isInitialized;
    private Task? _processingTask;
    private SerialPortSettings? _serialPortSettings;
    private const int MaxConcurrentSorting = 5;

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
            _serialPortSettings = _settingsService.LoadSettings<SerialPortSettings>();
            if (_serialPortSettings == null)
            {
                Log.Error("初始化失败：无法加载串口设置");
                return false;
            }
            
            // 初始化底层服务
            var result = await _carSortingService.InitializeAsync();
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
            // 创建新的取消令牌
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
            var tokenSource = new CancellationTokenSource();
            
            // 启动处理线程
            _processingTask = Task.Run(() => ProcessSortingQueueAsync(tokenSource.Token), tokenSource.Token);
            
            _isRunning = true;
            Log.Information("小车分拣服务已启动");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动小车分拣服务时发生异常");
            return Task.FromResult(false);
        }
    }
    
    public Task<bool> StopAsync()
    {
        if (!_isRunning)
        {
            Log.Information("分拣服务已停止");
            return Task.FromResult(true);
        }
        
        try
        {
            // 取消后台处理任务
            _cancellationTokenSource.Cancel();
            
            // 等待任务完成
            try
            {
                _processingTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException ae)
            {
                // 忽略任务取消异常
                if (!(ae.InnerExceptions is [TaskCanceledException]))
                {
                    Log.Warning("停止处理队列时发生异常: {Exception}", ae.Message);
                }
            }
            
            _isRunning = false;
            Log.Information("小车分拣服务已停止");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止小车分拣服务时发生异常");
            return Task.FromResult(false);
        }
    }
    
    public Task<bool> ProcessPackageSortingAsync(PackageInfo package)
    {
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
        // 此方法实现重置小车的功能
        // 可以清空当前队列，停止所有处理
        try
        {
            // 清空队列
            while (_sortingQueue.TryDequeue(out _)) { }
            
            // 记录当前正在处理的包裹
            var currentlyProcessing = _processingPackages.Values.ToList();
            _processingPackages.Clear();
            
            Log.Information("已重置小车分拣队列，清除了 {QueueCount} 个等待包裹和 {ProcessingCount} 个处理中包裹",
                _sortingQueue.Count, currentlyProcessing.Count);
            
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "重置小车分拣服务时发生异常");
            return Task.FromResult(false);
        }
    }
    
    /// <summary>
    /// 后台处理分拣队列
    /// </summary>
    private async Task ProcessSortingQueueAsync(CancellationToken cancellationToken)
    {
        Log.Information("开始处理分拣队列");
        
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
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _processingPackages[package.Index] = package;
                        await ProcessSinglePackageAsync(package);
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
                }, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("分拣队列处理已取消");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理分拣队列时发生异常");
        }
        
        Log.Information("分拣队列处理已停止");
    }
    
    /// <summary>
    /// 处理单个包裹的分拣命令
    /// </summary>
    private async Task ProcessSinglePackageAsync(PackageInfo package)
    {
        try
        {
            // 检查串口设置
            if (_serialPortSettings == null)
            {
                Log.Error("未加载串口设置，无法发送命令: {Barcode}", package.Barcode);
                return;
            }
            
            // 应用命令延迟
            var delayMs = _serialPortSettings.CommandDelayMs;
            if (delayMs > 0)
            {
                Log.Debug("延迟 {Delay}ms 后发送格口 {ChuteNumber} 的分拣命令", 
                    delayMs, package.ChuteNumber);
                await Task.Delay(delayMs);
            }
            
            // 发送分拣命令
            Log.Information("开始发送格口 {ChuteNumber} 的分拣命令，包裹: {Barcode}", 
                package.ChuteNumber, package.Barcode);
            var result = await _carSortingService.SendCommandForPackageAsync(package);
            
            if (result)
                Log.Information("包裹 {Barcode} 的分拣命令发送成功", package.Barcode);
            else
                Log.Warning("包裹 {Barcode} 的分拣命令发送失败", package.Barcode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹 {Barcode} 的分拣命令时发生异常", package.Barcode);
        }
    }
    
    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    
    /// <summary>
    /// 释放资源
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 停止服务
            _ = StopAsync();
            
            // 释放资源
            _cancellationTokenSource.Dispose();
            _processSemaphore.Dispose();
        }
    }
} 
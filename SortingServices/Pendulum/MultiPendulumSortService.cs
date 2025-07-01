using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Common.Models.Package;
using Common.Models.Settings.Sort.PendulumSort;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.TCP;
using Serilog;

namespace SortingServices.Pendulum;

/// <summary>
///     多光电多摆轮分拣服务实现
/// </summary>
public class MultiPendulumSortService(ISettingsService settingsService) : BasePendulumSortService(settingsService)
{
    private readonly ConcurrentDictionary<string, TcpClientService> _sortingClients = new();
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _isInitialized;

    // 为每个光电创建一个动作队列和处理任务
    private readonly ConcurrentDictionary<string, Channel<Func<Task>>> _actionChannels = new();
    private readonly ConcurrentDictionary<string, Task> _actionProcessorTasks = new();

    // 缓存配置以避免重复加载
    private PendulumSortConfig _config = new();

    public override async Task InitializeAsync(PendulumSortConfig configuration)
    {
        try
        {
            await _initializationLock.WaitAsync();

            if (_isInitialized)
            {
                Log.Debug("多光电多摆轮分拣服务已经初始化，跳过初始化操作");
                return;
            }

            Log.Information("开始初始化多光电多摆轮分拣服务...");

            // 缓存配置
            _config = configuration;

            // 初始化触发光电连接
            TriggerClient = new TcpClientService(
                "触发光电",
                configuration.TriggerPhotoelectric.IpAddress,
                configuration.TriggerPhotoelectric.Port,
                ProcessTriggerData,
                connected => UpdateDeviceConnectionState("触发光电", connected)
            );

            try
            {
                TriggerClient.Connect();
                Log.Debug("触发光电连接初始化完成");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "初始化触发光电连接失败");
                // 连接失败不抛出异常，后续会自动重连
            }

            // 初始化分拣光电连接和动作队列
            foreach (var photoelectric in configuration.SortingPhotoelectrics)
            {
                Log.Debug("开始初始化分拣光电 {Name} 连接", photoelectric.Name);
                var client = new TcpClientService(
                    photoelectric.Name,
                    photoelectric.IpAddress,
                    photoelectric.Port,
                    data => ProcessSortingData(data, photoelectric.Name),
                    connected => UpdateDeviceConnectionState(photoelectric.Name, connected)
                );

                _sortingClients[photoelectric.Name] = client;
                PendulumStates[photoelectric.Name] = new PendulumState();

                // 创建动作队列
                var channel = Channel.CreateUnbounded<Func<Task>>();
                _actionChannels[photoelectric.Name] = channel;

                try
                {
                    client.Connect();
                    Log.Debug("分拣光电 {Name} 连接初始化完成", photoelectric.Name);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "初始化分拣光电 {Name} 连接失败", photoelectric.Name);
                    // 连接失败不抛出异常，后续会自动重连
                }
            }

            _isInitialized = true;
            Log.Information("多光电多摆轮分拣服务初始化完成");
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public override async Task StartAsync()
    {
        if (IsRunningFlag)
        {
            Log.Debug("多光电多摆轮分拣服务已经在运行，跳过启动操作");
            return;
        }

        try
        {
            Log.Information("开始启动多光电多摆轮分拣服务...");

            // 确保服务已初始化
            if (!_isInitialized)
            {
                Log.Warning("多光电多摆轮分拣服务未初始化，尝试初始化");
                await InitializeAsync(_settingsService.LoadSettings<PendulumSortConfig>());
            }

            // 创建取消令牌
            CancellationTokenSource = new CancellationTokenSource();

            // 启动每个光电的动作处理队列
            foreach (var photoelectric in _config.SortingPhotoelectrics)
            {
                if (_actionChannels.TryGetValue(photoelectric.Name, out var channel))
                {
                    var processorTask = ProcessActionQueueAsync(photoelectric.Name, channel.Reader, CancellationTokenSource.Token);
                    _actionProcessorTasks[photoelectric.Name] = processorTask;
                    Log.Debug("已启动分拣光电 {Name} 的动作处理队列", photoelectric.Name);
                }
            }

            // 在启动服务前直接发送启动命令到所有连接的分拣光电
            try
            {
                foreach (var client in _sortingClients)
                {
                    if (!client.Value.IsConnected())
                    {
                        Log.Warning("分拣光电 {Name} 未连接，跳过发送启动命令", client.Key);
                        continue;
                    }

                    var startCommand = GetCommandBytes(PendulumCommands.Module2.Start);
                    client.Value.Send(startCommand);
                    var resetLeftCommand = GetCommandBytes(PendulumCommands.Module2.ResetLeft);
                    var resetRightCommand = GetCommandBytes(PendulumCommands.Module2.ResetRight);
                    client.Value.Send(resetLeftCommand);
                    client.Value.Send(resetRightCommand);
                    Log.Information("已发送启动和回正命令到分拣光电 {Name}", client.Key);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "向分拣光电发送启动命令失败");
            }

            // 启动超时检查定时器
            TimeoutCheckTimer.Start();
            Log.Debug("超时检查定时器已启动");

            IsRunningFlag = true;

            // 启动主循环
            _ = Task.Run(async () =>
            {
                try
                {
                    Log.Debug("多光电多摆轮分拣服务主循环开始运行");
                    // 主循环
                    while (!CancellationTokenSource.Token.IsCancellationRequested)
                    {
                        try
                        {
                            // 检查设备连接状态
                            if (TriggerClient != null && !TriggerClient.IsConnected())
                            {
                                Log.Warning("触发光电连接断开，尝试重连");
                                await ReconnectAsync();
                            }

                            foreach (var client in _sortingClients.Where(static client => !client.Value.IsConnected()))
                            {
                                Log.Warning("分拣光电 {Name} 连接断开，尝试重连", client.Key);
                                await ReconnectAsync();
                            }

                            await Task.Delay(1000, CancellationTokenSource.Token); // 每秒检查一次连接状态
                        }
                        catch (OperationCanceledException)
                        {
                            Log.Debug("多光电多摆轮分拣服务主循环收到取消信号");
                            break;
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "多光电多摆轮分拣服务主循环发生错误");
                            await Task.Delay(1000, CancellationTokenSource.Token); // 发生错误时等待一秒再继续
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "多光电多摆轮分拣服务主循环发生致命错误");
                }
                finally
                {
                    IsRunningFlag = false;
                    Log.Debug("多光电多摆轮分拣服务主循环结束运行");
                }
            }, CancellationTokenSource.Token);

            Log.Information("多光电多摆轮分拣服务启动完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动多光电多摆轮分拣服务失败");
            throw;
        }
    }

    /// <summary>
    /// 为每个摆轮处理动作队列的专用方法
    /// </summary>
    private async Task ProcessActionQueueAsync(string photoelectricName, ChannelReader<Func<Task>> reader, CancellationToken token)
    {
        Log.Debug("开始处理分拣光电 {Name} 的动作队列", photoelectricName);
        
        try
        {
            await foreach (var action in reader.ReadAllAsync(token))
            {
                try
                {
                    // 顺序执行队列中的每一个动作
                    await action();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理摆轮 {Name} 的动作时发生错误", photoelectricName);
                    // 发生错误时强制复位状态
                    if (PendulumStates.TryGetValue(photoelectricName, out var state))
                    {
                        state.ForceReset();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Debug("分拣光电 {Name} 的动作队列处理被取消", photoelectricName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "分拣光电 {Name} 的动作队列处理发生致命错误", photoelectricName);
        }
        finally
        {
            Log.Debug("分拣光电 {Name} 的动作队列处理结束", photoelectricName);
        }
    }

    public override async Task StopAsync()
    {
        if (!IsRunningFlag)
        {
            Log.Debug("多光电多摆轮分拣服务未运行，跳过停止操作");
            return;
        }

        try
        {
            Log.Information("开始停止多光电多摆轮分拣服务...");

            // 先向所有分拣光电发送停止命令，确保在改变服务状态前发送
            foreach (var client in _sortingClients.Where(static client => client.Value.IsConnected()))
            {
                try
                {
                    // 先发送停止命令
                    var stopCommand = GetCommandBytes(PendulumCommands.Module2.Stop);
                    client.Value.Send(stopCommand);

                    // 然后发送回正指令
                    var resetLeftCommand = GetCommandBytes(PendulumCommands.Module2.ResetLeft);
                    var resetRightCommand = GetCommandBytes(PendulumCommands.Module2.ResetRight);
                    client.Value.Send(resetLeftCommand);
                    client.Value.Send(resetRightCommand);

                    Log.Information("已发送停止和回正命令到分拣光电 {Name}", client.Key);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "向分拣光电 {Name} 发送停止命令失败", client.Key);
                }
            }

            // 停止主循环
            if (CancellationTokenSource != null)
            {
                await CancellationTokenSource.CancelAsync();
                Log.Debug("已发送取消信号到主循环");
            }

            // 关闭所有动作队列并等待处理任务完成
            foreach (var (photoelectricName, channel) in _actionChannels)
            {
                try
                {
                    channel.Writer.Complete();
                    Log.Debug("已关闭分拣光电 {Name} 的动作队列", photoelectricName);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "关闭分拣光电 {Name} 的动作队列时发生错误", photoelectricName);
                }
            }

            // 等待所有动作处理任务完成
            var waitTasks = _actionProcessorTasks.Values.ToArray();
            if (waitTasks.Length > 0)
            {
                try
                {
                    await Task.WhenAll(waitTasks).WaitAsync(TimeSpan.FromSeconds(5));
                    Log.Debug("所有动作处理任务已完成");
                }
                catch (TimeoutException)
                {
                    Log.Warning("等待动作处理任务完成超时");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "等待动作处理任务完成时发生错误");
                }
            }

            IsRunningFlag = false;

            // 停止超时检查定时器
            TimeoutCheckTimer.Stop();
            Log.Debug("超时检查定时器已停止");

            // 清空处理中的包裹
            ProcessingPackages.Clear();
            PendingSortPackages.Clear();

            // 停止所有计时器
            foreach (var timer in PackageTimers.Values)
            {
                timer.Stop();
                timer.Dispose();
            }

            PackageTimers.Clear();
            _actionProcessorTasks.Clear();

            Log.Information("多光电多摆轮分拣服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止多光电多摆轮分拣服务时发生错误");
            throw;
        }
    }

    private void ProcessTriggerData(byte[] data)
    {
        try
        {
            var message = Encoding.ASCII.GetString(data);
            Log.Debug("收到触发光电数据: {Message}", message);

            // 使用基类的信号处理方法
            HandlePhotoelectricSignal(message, "触发光电");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理触发光电数据时发生错误");
        }
    }

    private void ProcessSortingData(byte[] data, string photoelectricName)
    {
        try
        {
            var message = Encoding.ASCII.GetString(data);
            
            // 增加防抖逻辑，与触发光电保持一致
            var config = _settingsService.LoadSettings<PendulumSortConfig>();
            var debounceTime = config.GlobalDebounceTime;
            var now = DateTime.Now;

            if (_lastSignalTimes.TryGetValue(photoelectricName, out var lastSignalTime))
            {
                if ((now - lastSignalTime).TotalMilliseconds < debounceTime)
                {
                    Log.Debug("分拣光电 {PhotoelectricName} 在 {DebounceTime}ms 防抖时间内收到重复信号 '{Signal}'，已忽略.",
                        photoelectricName, debounceTime, message);
                    return; // 忽略此信号
                }
            }
            _lastSignalTimes[photoelectricName] = now; // 更新上次信号时间
            
            // 只对上升沿信号（OCCH2:1）做出反应
            if (!message.Contains("OCCH2:1")) 
            {
                // 记录Debug日志表示收到了其他信号，但不会处理
                Log.Debug("收到分拣光电 {Name} 的非上升沿信号: {Message}，已忽略", photoelectricName, message);
                return;
            }

            var sortingTime = DateTime.Now;
            Log.Information("分拣光电 {Name} 收到上升沿信号，开始匹配包裹", photoelectricName);
            
            // 触发分拣光电信号事件
            RaiseSortingPhotoelectricSignal(photoelectricName, sortingTime);
            
            // 使用基类的匹配逻辑
            var newPackage = MatchPackageForSorting(photoelectricName);
            if (newPackage == null) return;
            
            var matchTime = DateTime.Now;
            var timeSinceTrigger = matchTime - newPackage.TriggerTimestamp;

            Log.Information("分拣光电 {Name} 匹配到包裹 {Index}|{Barcode} (耗时: {MatchDuration:F2}ms)",
                photoelectricName, newPackage.Index, newPackage.Barcode, timeSinceTrigger.TotalMilliseconds);
            
            // 将 ExecuteSortingAction 的调用包装成一个 Func<Task> 并放入队列
            if (_actionChannels.TryGetValue(photoelectricName, out var channel))
            {
                // 注意，这里传递的是当时的数据快照，避免闭包问题
                var packageSnapshot = newPackage; 
                var success = channel.Writer.TryWrite(async () => await ExecuteSortingAction(packageSnapshot, photoelectricName));
                
                if (!success)
                {
                    Log.Error("无法将分拣动作加入队列，光电 {Name} 的队列可能已关闭", photoelectricName);
                }
            }
            else
            {
                Log.Error("未找到分拣光电 {Name} 的动作队列", photoelectricName);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理分拣光电 {Name} 数据时发生错误", photoelectricName);
        }
    }

    /// <summary>
    ///     不处理第二光电信号
    /// </summary>
    protected override void HandleSecondPhotoelectric(string data)
    {
        // 多摆轮不处理第二光电信号
    }

    /// <summary>
    ///     重新连接设备
    /// </summary>
    protected override Task ReconnectAsync()
    {
        try
        {
            // 重连触发光电
            if (TriggerClient != null)
            {
                TriggerClient.Dispose();
                TriggerClient = new TcpClientService(
                    "触发光电",
                    _config.TriggerPhotoelectric.IpAddress,
                    _config.TriggerPhotoelectric.Port,
                    ProcessTriggerData,
                    connected => UpdateDeviceConnectionState("触发光电", connected)
                );
                TriggerClient.Connect();
            }

            // 重连分拣光电
            foreach (var photoelectric in _config.SortingPhotoelectrics)
            {
                if (!_sortingClients.TryGetValue(photoelectric.Name, out var client) || client.IsConnected()) continue;

                client.Dispose();
                var newClient = new TcpClientService(
                    photoelectric.Name,
                    photoelectric.IpAddress,
                    photoelectric.Port,
                    data => ProcessSortingData(data, photoelectric.Name),
                    connected => UpdateDeviceConnectionState(photoelectric.Name, connected)
                );
                _sortingClients[photoelectric.Name] = newClient;
                newClient.Connect();

                // 如果服务正在运行，发送启动命令和回正命令
                if (!IsRunningFlag) continue;

                var startCommand = GetCommandBytes(PendulumCommands.Module2.Start);
                newClient.Send(startCommand);
                var resetLeftCommand = GetCommandBytes(PendulumCommands.Module2.ResetLeft);
                var resetRightCommand = GetCommandBytes(PendulumCommands.Module2.ResetRight);
                newClient.Send(resetLeftCommand);
                newClient.Send(resetRightCommand);
                Log.Debug("重连后已发送启动和回正命令到分拣光电 {Name}", photoelectric.Name);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "重连设备失败");
        }

        return Task.CompletedTask;
    }

    protected override bool SlotBelongsToPhotoelectric(int targetSlot, string photoelectricName)
    {
        // 获取分拣光电的配置
        var photoelectric = _config.SortingPhotoelectrics.FirstOrDefault(p => p.Name == photoelectricName);
        if (photoelectric == null) return false;

        // 获取分拣光电的索引（从0开始）
        var index = _config.SortingPhotoelectrics.IndexOf(photoelectric);
        if (index < 0) return false;

        // 每个分拣光电处理两个格口
        // 第一个光电处理1、2号格口，第二个处理3、4号格口，以此类推
        var startSlot = index * 2 + 1;
        var endSlot = startSlot + 1;
        return targetSlot >= startSlot && targetSlot <= endSlot;
    }

    protected override TcpClientService? GetSortingClient(string photoelectricName)
    {
        return _sortingClients.TryGetValue(photoelectricName, out var client) ? client : null;
    }

    protected override string? GetPhotoelectricNameBySlot(int slot)
    {
        // 计算光电索引（从0开始）
        var index = (slot - 1) / 2;

        // 获取对应索引的光电配置
        var photoelectric = _config.SortingPhotoelectrics.ElementAtOrDefault(index);
        return photoelectric?.Name;
    }


}
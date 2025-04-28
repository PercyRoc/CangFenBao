using System.Collections.Concurrent;
using System.Text;
using Common.Models.Settings.Sort.PendulumSort;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.TCP;
using Serilog;

namespace SortingServices.Pendulum;

/// <summary>
///     多光电多摆轮分拣服务实现
/// </summary>
public class MultiPendulumSortService : BasePendulumSortService
{
    private readonly ConcurrentDictionary<string, TcpClientService> _sortingClients = new();
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _isInitialized;
    private readonly ISettingsService _settingsService;

    // 记录光电信号状态的字典，true 表示高电平，false 表示低电平
    private readonly ConcurrentDictionary<string, bool> _photoelectricSignalStates = new();

    // 记录上一次信号状态的字典
    private readonly ConcurrentDictionary<string, bool> _previousPhotoelectricSignalStates = new();

    public MultiPendulumSortService(ISettingsService settingsService) 
        : base(settingsService)
    {
        _settingsService = settingsService;
    }

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

            // 初始化触发光电连接
            TriggerClient = new TcpClientService(
                "触发光电",
                configuration.TriggerPhotoelectric.IpAddress,
                configuration.TriggerPhotoelectric.Port,
                ProcessTriggerData,
                connected => UpdateDeviceConnectionState("触发光电", connected)
            );

            // 初始化触发光电信号状态
            _photoelectricSignalStates["触发光电"] = false;
            _previousPhotoelectricSignalStates["触发光电"] = false;

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

            // 初始化分拣光电连接
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

                // 初始化分拣光电信号状态
                _photoelectricSignalStates[photoelectric.Name] = false;
                _previousPhotoelectricSignalStates[photoelectric.Name] = false;

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

            // 创建取消令牌
            CancellationTokenSource = new CancellationTokenSource();
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

            // 更新触发光电信号状态
            if (message.Contains("OCCH2:1"))
            {
                // 高电平
                UpdatePhotoelectricSignalState("触发光电", true);
            }
            else if (message.Contains("OCCH2:0"))
            {
                // 低电平
                UpdatePhotoelectricSignalState("触发光电", false);
            }

            // 使用基类的信号处理方法
            HandlePhotoelectricSignal(message);
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
            Log.Information("收到分拣光电 {Name} 数据: {Message}", photoelectricName, message);

            // 更新分拣光电信号状态
            if (!message.Contains("OCCH2:1")) return;
            // 高电平
            UpdatePhotoelectricSignalState(photoelectricName, true);
            Log.Information("分拣光电 {Name} 收到上升沿信号，开始匹配包裹并执行分拣", photoelectricName);
            // 使用基类的匹配逻辑
            var package = MatchPackageForSorting(photoelectricName);
            if (package == null) return;
            // Calculate the time difference
            var matchTime = DateTime.Now;
            var timeSinceTrigger = matchTime - package.TriggerTimestamp;

            Log.Information("分拣光电 {Name} 匹配到包裹 {Barcode} (耗时: {MatchDuration:F2}ms)，开始执行分拣动作",
                photoelectricName, package.Barcode, timeSinceTrigger.TotalMilliseconds);
            // 直接执行分拣动作
            _ = ExecuteSortingAction(package, photoelectricName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理分拣光电 {Name} 数据时发生错误", photoelectricName);
        }
    }

    /// <summary>
    /// 更新光电信号状态并检测异常情况
    /// </summary>
    /// <param name="photoelectricName">光电名称</param>
    /// <param name="isHighLevel">是否为高电平</param>
    private void UpdatePhotoelectricSignalState(string photoelectricName, bool isHighLevel)
    {
        // 获取上一次的信号状态
        _previousPhotoelectricSignalStates.TryGetValue(photoelectricName, out var previousState);

        // 如果当前是低电平，且上一次也是低电平，记录警告 (改为 Debug)
        if (!isHighLevel && !previousState)
        {
            Log.Debug("光电 {Name} 连续低电平信号.", photoelectricName);
        }

        // 记录当前信号状态
        Log.Debug("光电 {Name} 信号状态: {State}", photoelectricName, isHighLevel ? "高电平" : "低电平");

        // 更新状态记录
        _previousPhotoelectricSignalStates[photoelectricName] = _photoelectricSignalStates[photoelectricName];
        _photoelectricSignalStates[photoelectricName] = isHighLevel;
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
                    _settingsService.LoadSettings<PendulumSortConfig>().TriggerPhotoelectric.IpAddress,
                    _settingsService.LoadSettings<PendulumSortConfig>().TriggerPhotoelectric.Port,
                    ProcessTriggerData,
                    connected => UpdateDeviceConnectionState("触发光电", connected)
                );
                TriggerClient.Connect();
            }

            // 重连分拣光电
            foreach (var photoelectric in _settingsService.LoadSettings<PendulumSortConfig>().SortingPhotoelectrics)
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
        var photoelectric = _settingsService.LoadSettings<PendulumSortConfig>().SortingPhotoelectrics.FirstOrDefault(p => p.Name == photoelectricName);
        if (photoelectric == null) return false;

        // 获取分拣光电的索引（从0开始）
        var index = _settingsService.LoadSettings<PendulumSortConfig>().SortingPhotoelectrics.IndexOf(photoelectric);
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

    protected override void CheckSortingPhotoelectricChanges(PendulumSortConfig oldConfig, PendulumSortConfig newConfig)
    {
        // 检查是否有分拣光电的连接参数发生变化
        var hasChanges = newConfig.SortingPhotoelectrics.Any(newPhotoelectric =>
        {
            var oldPhotoelectric = oldConfig.SortingPhotoelectrics.FirstOrDefault(p => p.Name == newPhotoelectric.Name);
            if (oldPhotoelectric == null) return false;

            if (oldPhotoelectric.IpAddress == newPhotoelectric.IpAddress &&
                oldPhotoelectric.Port == newPhotoelectric.Port) return false;

            Log.Information("分拣光电 {Name} 连接参数已变更，准备重新连接", newPhotoelectric.Name);
            return true;
        });

        if (hasChanges)
        {
            _ = ReconnectAsync();
        }
    }

    protected override string? GetPhotoelectricNameBySlot(int slot)
    {
        // 计算光电索引（从0开始）
        var index = (slot - 1) / 2;

        // 获取对应索引的光电配置
        var photoelectric = _settingsService.LoadSettings<PendulumSortConfig>().SortingPhotoelectrics.ElementAtOrDefault(index);
        return photoelectric?.Name;
    }
}
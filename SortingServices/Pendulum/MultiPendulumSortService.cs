using System.Collections.Concurrent;
using System.Text;
using Common.Services.Settings;
using Serilog;
using SortingServices.Common;
using SortingServices.Pendulum.Models;

namespace SortingServices.Pendulum;

/// <summary>
///     多光电多摆轮分拣服务实现
/// </summary>
public class MultiPendulumSortService(ISettingsService settingsService) : BasePendulumSortService(settingsService)
{
    private readonly ConcurrentDictionary<string, TcpClientService> _sortingClients = new();

    public override Task InitializeAsync(PendulumSortConfig configuration)
    {
        Configuration = configuration;

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
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化触发光电连接失败");
            // 连接失败不抛出异常，后续会自动重连
        }

        // 初始化分拣光电连接
        foreach (var photoelectric in configuration.SortingPhotoelectrics)
        {
            var client = new TcpClientService(
                photoelectric.Name,
                photoelectric.IpAddress,
                photoelectric.Port,
                data => ProcessSortingData(data, photoelectric.Name),
                connected => UpdateDeviceConnectionState(photoelectric.Name, connected)
            );

            _sortingClients[photoelectric.Name] = client;
            PendulumStates[photoelectric.Name] = new PendulumState();

            try
            {
                client.Connect();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "初始化分拣光电 {Name} 连接失败", photoelectric.Name);
                // 连接失败不抛出异常，后续会自动重连
            }
        }

        Log.Information("多光电多摆轮分拣服务初始化完成");
        return Task.CompletedTask;
    }

    public override async Task StartAsync()
    {
        if (IsRunningFlag) return;

        // 启动超时检查定时器
        TimeoutCheckTimer.Start();

        // 创建取消令牌
        CancellationTokenSource = new CancellationTokenSource();
        IsRunningFlag = true;

        // 启动主循环
        _ = Task.Run(async () =>
        {
            try
            {
                // 向所有分拣光电发送启动命令和回正命令
                foreach (var client in _sortingClients)
                {
                    if (!client.Value.IsConnected()) continue;
                    var startCommand = GetCommandBytes(PendulumCommands.Module2.Start);
                    client.Value.Send(startCommand);
                    var resetLeftCommand = GetCommandBytes(PendulumCommands.Module2.ResetLeft);
                    var resetRightCommand = GetCommandBytes(PendulumCommands.Module2.ResetRight);
                    client.Value.Send(resetLeftCommand);
                    client.Value.Send(resetRightCommand);
                    Log.Debug("已发送启动和回正命令到分拣光电 {Name}", client.Key);
                }

                // 主循环
                while (!CancellationTokenSource.Token.IsCancellationRequested)
                {
                    // 检查设备连接状态
                    if (TriggerClient != null && !TriggerClient.IsConnected())
                    {
                        Log.Warning("触发光电连接断开，尝试重连");
                        await ReconnectAsync();
                    }

                    foreach (var client in _sortingClients)
                    {
                        if (client.Value.IsConnected()) continue;
                        Log.Warning("分拣光电 {Name} 连接断开，尝试重连", client.Key);
                        await ReconnectAsync();
                    }

                    await Task.Delay(1000, CancellationTokenSource.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消，不做处理
            }
            catch (Exception ex)
            {
                Log.Error(ex, "多光电多摆轮分拣服务发生错误");
            }
            finally
            {
                IsRunningFlag = false;
            }
        }, CancellationTokenSource.Token);

        await Task.CompletedTask;
    }

    public override async Task StopAsync()
    {
        if (!IsRunningFlag) return;

        try
        {
            // 停止主循环
            await CancellationTokenSource?.CancelAsync()!;
            IsRunningFlag = false;

            // 停止超时检查定时器
            TimeoutCheckTimer.Stop();

            // 向所有分拣光电发送回正命令和停止命令
            foreach (var client in _sortingClients)
            {
                if (!client.Value.IsConnected()) continue;
                try
                {
                    // 发送左右回正指令
                    var resetLeftCommand = GetCommandBytes(PendulumCommands.Module2.ResetLeft);
                    var resetRightCommand = GetCommandBytes(PendulumCommands.Module2.ResetRight);
                    client.Value.Send(resetLeftCommand);
                    client.Value.Send(resetRightCommand);

                    // 发送停止命令
                    var stopCommand = GetCommandBytes(PendulumCommands.Module2.Stop);
                    client.Value.Send(stopCommand);
                    Log.Debug("已发送回正和停止命令到分拣光电 {Name}", client.Key);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "向分拣光电 {Name} 发送回正和停止命令失败", client.Key);
                }
            }

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

    public override async Task<bool> UpdateConfigurationAsync(PendulumSortConfig configuration)
    {
        try
        {
            var wasRunning = IsRunningFlag;

            // 如果服务正在运行，先停止
            if (wasRunning) await StopAsync();

            // 更新配置
            Configuration = configuration;

            // 如果之前在运行，重新启动
            if (wasRunning)
            {
                await InitializeAsync(configuration);
                await StartAsync();
            }

            Log.Information("多光电多摆轮分拣服务配置已更新");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新多光电多摆轮分拣服务配置失败");
            return false;
        }
    }

    private void ProcessTriggerData(byte[] data)
    {
        try
        {
            var message = Encoding.ASCII.GetString(data);
            Log.Debug("收到触发光电数据: {Message}", message);

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

            if (message.Contains("OCCH1:1"))
            {
                Log.Information("分拣光电 {Name} 收到上升沿信号，开始匹配包裹", photoelectricName);
                // 使用基类的匹配逻辑
                var package = MatchPackageForSorting(photoelectricName);
                if (package != null)
                {
                    // 临时存储匹配到的包裹
                    MatchedPackages[photoelectricName] = package;
                    Log.Information("分拣光电 {Name} 匹配到包裹 {Barcode}，等待下降沿信号",
                        photoelectricName, package.Barcode);
                }
                else
                {
                    Log.Warning("分拣光电 {Name} 未匹配到包裹", photoelectricName);
                    // 打印当前待分拣队列状态
                    var pendingPackages = PendingSortPackages.Values
                        .Where(p => !IsPackageProcessing(p.Barcode))
                        .OrderBy(p => p.TriggerTimestamp)
                        .ThenBy(p => p.Index)
                        .ToList();

                    if (pendingPackages.Count != 0)
                    {
                        Log.Information("当前待分拣队列中有 {Count} 个包裹:", pendingPackages.Count);
                        foreach (var pkg in pendingPackages)
                            Log.Information("包裹 {Barcode}(序号:{Index}) 触发时间:{TriggerTime:HH:mm:ss.fff} 目标格口:{Slot}",
                                pkg.Barcode, pkg.Index, pkg.TriggerTimestamp, pkg.ChuteName);
                    }
                    else
                    {
                        Log.Information("当前待分拣队列为空");
                    }
                }
            }

            if (!message.Contains("OCCH1:0")) return;
            {
                Log.Information("分拣光电 {Name} 收到下降沿信号，准备执行分拣动作", photoelectricName);
                // 检查是否有匹配到的包裹
                if (!MatchedPackages.TryRemove(photoelectricName, out var package))
                {
                    Log.Warning("分拣光电 {Name} 未找到匹配的包裹，无法执行分拣动作", photoelectricName);
                    return;
                }

                Log.Information("分拣光电 {Name} 开始处理包裹 {Barcode} 的分拣动作",
                    photoelectricName, package.Barcode);

                // 执行分拣动作
                _ = ExecuteSortingAction(package, photoelectricName);
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
                    Configuration.TriggerPhotoelectric.IpAddress,
                    Configuration.TriggerPhotoelectric.Port,
                    ProcessTriggerData,
                    connected => UpdateDeviceConnectionState("触发光电", connected)
                );
                TriggerClient.Connect();
            }

            // 重连分拣光电
            foreach (var photoelectric in Configuration.SortingPhotoelectrics)
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
        var photoelectric = Configuration.SortingPhotoelectrics.FirstOrDefault(p => p.Name == photoelectricName);
        if (photoelectric == null) return false;

        // 获取分拣光电的索引（从0开始）
        var index = Configuration.SortingPhotoelectrics.IndexOf(photoelectric);
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
        // 检查每个分拣光电的连接参数是否变化
        foreach (var newPhotoelectric in newConfig.SortingPhotoelectrics)
        {
            var oldPhotoelectric = oldConfig.SortingPhotoelectrics.FirstOrDefault(p => p.Name == newPhotoelectric.Name);
            if (oldPhotoelectric == null) continue;

            if (oldPhotoelectric.IpAddress == newPhotoelectric.IpAddress &&
                oldPhotoelectric.Port == newPhotoelectric.Port) continue;
            Log.Information("分拣光电 {Name} 连接参数已变更，准备重新连接", newPhotoelectric.Name);
            _ = ReconnectAsync();
            break; // 发现一个变化就重连所有设备
        }
    }

    protected override string? GetPhotoelectricNameBySlot(int slot)
    {
        // 计算光电索引（从0开始）
        var index = (slot - 1) / 2;

        // 获取对应索引的光电配置
        var photoelectric = Configuration.SortingPhotoelectrics.ElementAtOrDefault(index);
        return photoelectric?.Name;
    }
}
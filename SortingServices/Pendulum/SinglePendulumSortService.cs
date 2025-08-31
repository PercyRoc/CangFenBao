using System.Collections.Concurrent;
using System.Text;
using Common.Models.Settings.Sort.PendulumSort;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.TCP;
using Prism.Events;
using Serilog;

namespace SortingServices.Pendulum;

/// <summary>
///     单光电单摆轮分拣服务实现
/// </summary>
public class SinglePendulumSortService(ISettingsService settingsService, IEventAggregator eventAggregator)
    : BasePendulumSortService(settingsService, eventAggregator)
{
    // 记录光电信号状态的字典，true 表示高电平，false 表示低电平
    private readonly ConcurrentDictionary<string, bool> _photoelectricSignalStates = new();

    public override Task InitializeAsync(PendulumSortConfig configuration)
    {
        // 初始化触发光电连接（禁用自动重连）
        TriggerClient = new TcpClientService(
            "触发光电",
            configuration.TriggerPhotoelectric.IpAddress,
            configuration.TriggerPhotoelectric.Port,
            ProcessTriggerData,
            connected => UpdateDeviceConnectionState("触发光电", connected),
            false
        );

        try
        {
            TriggerClient.Connect();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化触发光电连接失败");
            // 连接失败不抛出异常，已禁用自动重连
        }

        // 初始化摆轮状态
        PendulumStates["默认"] = new PendulumState();

        // 初始化光电信号状态
        _photoelectricSignalStates["触发光电"] = false;

        Log.Information("单光电单摆轮分拣服务初始化完成");
        return Task.CompletedTask;
    }

    public override Task StartAsync()
    {
        if (IsRunningFlag) return Task.CompletedTask;

        // 在启动服务前先发送启动命令
        if (TriggerClient != null && TriggerClient.IsConnected())
            try
            {
                var startCommand = GetCommandBytes(PendulumCommands.Module2.Start);
                TriggerClient.Send(startCommand);
                var resetLeftCommand = GetCommandBytes(PendulumCommands.Module2.ResetLeft);
                var resetRightCommand = GetCommandBytes(PendulumCommands.Module2.ResetRight);
                TriggerClient.Send(resetLeftCommand);
                TriggerClient.Send(resetRightCommand);
                Log.Information("已发送启动命令到触发光电");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "发送启动命令到触发光电失败");
            }
        else
            Log.Warning("触发光电未连接，将在连接后自动发送启动命令");

        // 启动超时检查定时器
        TimeoutCheckTimer.Start();

        // 创建取消令牌
        CancellationTokenSource = new CancellationTokenSource();
        IsRunningFlag = true;

        // 启动消费者任务
        StartConsumer();

        // 启动主循环
        _ = Task.Run(async () =>
        {
            try
            {
                // 主循环
                while (!CancellationTokenSource.Token.IsCancellationRequested)
                {
                    // 检查设备连接状态
                    if (TriggerClient != null && !TriggerClient.IsConnected())
                    {
                        Log.Warning("触发光电连接断开，已禁用重连逻辑，跳过重连");
                        // 已取消重连逻辑，直接标记为断开状态
                        UpdateDeviceConnectionState("触发光电", false);
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
                Log.Error(ex, "单光电单摆轮分拣服务发生错误");
            }
            finally
            {
                IsRunningFlag = false;
            }
        }, CancellationTokenSource.Token).ContinueWith(t =>
        {
            if (t is { IsFaulted: true, Exception: not null }) Log.Warning(t.Exception, "单光电单摆轮分拣服务主循环任务发生未观察的异常");
        }, TaskContinuationOptions.OnlyOnFaulted);

        return Task.CompletedTask;
    }

    public override async Task StopAsync()
    {
        if (!IsRunningFlag) return;

        try
        {
            // 先向触发光电发送停止命令，确保在改变服务状态前发送
            if (TriggerClient != null && TriggerClient.IsConnected())
                try
                {
                    // 先发送停止命令
                    var stopCommand = GetCommandBytes(PendulumCommands.Module2.Stop);
                    TriggerClient.Send(stopCommand);
                    Log.Information("已发送停止命令到触发光电");

                    // 然后发送回正指令
                    var resetLeftCommand = GetCommandBytes(PendulumCommands.Module2.ResetLeft);
                    var resetRightCommand = GetCommandBytes(PendulumCommands.Module2.ResetRight);
                    TriggerClient.Send(resetLeftCommand);
                    TriggerClient.Send(resetRightCommand);
                    Log.Information("已发送左右回正命令到触发光电");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "向触发光电发送停止命令失败");
                }
            else
                Log.Warning("触发光电未连接，无法发送停止命令");

            // 停止主循环
            if (CancellationTokenSource != null) await CancellationTokenSource.CancelAsync();

            // 停止消费者任务
            await StopConsumer();

            IsRunningFlag = false;

            // 停止超时检查定时器
            TimeoutCheckTimer.Stop();

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

            Log.Information("单光电单摆轮分拣服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止单光电单摆轮分拣服务时发生错误");
            throw;
        }
    }

    private void ProcessTriggerData(byte[] data)
    {
        try
        {
            var message = Encoding.ASCII.GetString(data);
            Log.Debug("收到触发光电数据: {Message}", message);

            // 只处理高电平信号，忽略低电平和其他信号
            bool isHighLevelSignal;
            string signalType;

            if (message.Contains("OCCH1:1"))
            {
                isHighLevelSignal = true;
                signalType = "OCCH1高电平";
            }
            else if (message.Contains("OCCH2:1"))
            {
                isHighLevelSignal = true;
                signalType = "OCCH2高电平";
            }
            else if (message.Contains("OCCH1:0") || message.Contains("OCCH2:0"))
            {
                // 低电平信号，直接忽略
                Log.Debug("触发光电收到低电平信号 '{Message}'，已忽略", message);
                return;
            }
            else
            {
                // 其他未知信号，直接忽略
                Log.Debug("触发光电收到未知信号 '{Message}'，已忽略", message);
                return;
            }

            // 只处理高电平信号
            if (isHighLevelSignal)
            {
                // 更新触发光电信号状态（只对高电平信号）
                UpdatePhotoelectricSignalState("触发光电", true);

                Log.Debug("触发光电收到有效高电平信号: {SignalType} - {Message}", signalType, message);

                // 使用基类的信号处理方法
                HandlePhotoelectricSignal(message, "触发光电");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理触发光电数据时发生错误");
        }
    }

    /// <summary>
    ///     处理第二光电信号
    /// </summary>
    protected override void HandleSecondPhotoelectric(string data)
    {
        var sortingTime = DateTime.Now;
        Log.Debug("单摆轮模式收到第二光电信号(OCCH2)，准备入队处理: {Data}", data);

        // 【生产者】将信号放入队列，由后台消费者任务处理
        // 在单摆轮模式中，分拣光电的名称约定为 "默认"
        EnqueueSortingSignal("默认", sortingTime);
    }

    protected override async Task ReconnectAsync()
    {
        try
        {
            var config = SettingsService.LoadSettings<PendulumSortConfig>();
            // 检查触发光电配置是否有效
            if (string.IsNullOrEmpty(config.TriggerPhotoelectric.IpAddress) ||
                config.TriggerPhotoelectric.Port == 0)
            {
                Log.Warning("触发光电未配置，跳过重连");
                return;
            }

            // 重新创建触发光电客户端（禁用自动重连）
            TriggerClient = new TcpClientService(
                "触发光电",
                config.TriggerPhotoelectric.IpAddress,
                config.TriggerPhotoelectric.Port,
                ProcessTriggerData,
                connected => UpdateDeviceConnectionState("触发光电", connected),
                false
            );
            await Task.Run(() => TriggerClient.Connect()).ContinueWith(t =>
            {
                if (t is { IsFaulted: true, Exception: not null }) Log.Warning(t.Exception, "单光电单摆轮重连任务发生未观察的异常");
            }, TaskContinuationOptions.OnlyOnFaulted);

            // 如果服务正在运行，发送启动命令
            if (IsRunningFlag)
            {
                var startCommand = GetCommandBytes(PendulumCommands.Module2.Start);
                TriggerClient.Send(startCommand);
                Log.Debug("重连后已发送启动命令到触发光电");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "重连设备失败");
        }
    }

    protected override bool SlotBelongsToPhotoelectric(int targetSlot, string photoelectricName)
    {
        // 单摆轮只处理1、2号格口
        return targetSlot is 1 or 2;
    }

    protected override TriggerPhotoelectric GetPhotoelectricConfig(string photoelectricName)
    {
        // 单摆轮使用触发光电的配置
        return SettingsService.LoadSettings<PendulumSortConfig>().TriggerPhotoelectric;
    }

    protected override TcpClientService? GetSortingClient(string photoelectricName)
    {
        return TriggerClient;
    }

    protected override string? GetPhotoelectricNameBySlot(int slot)
    {
        // 单光电单摆轮模式下，只处理1号和2号格口。其他格口为直行。
        if (slot is 1 or 2) return "默认";
        // 对于所有其他格口，返回null，这些包裹将被视为直行包裹
        return null;
    }


    /// <summary>
    ///     更新光电信号状态并检测异常情况
    /// </summary>
    /// <param name="photoelectricName">光电名称</param>
    /// <param name="isHighLevel">是否为高电平</param>
    private void UpdatePhotoelectricSignalState(string photoelectricName, bool isHighLevel)
    {
        // 获取当前记录的信号状态，如果不存在则默认为false（低电平）
        var currentState = _photoelectricSignalStates.GetValueOrDefault(photoelectricName, false);

        // 检查是否为连续两次低电平：当前记录状态为低电平，且新信号也是低电平
        if (!currentState && !isHighLevel)
            Log.Error("【光电信号异常】光电 {Name} 连续收到两次低电平信号，可能存在硬件故障或信号传输问题。请检查光电设备状态。", photoelectricName);

        // 记录当前信号状态
        Log.Debug("光电 {Name} 信号状态: {State}", photoelectricName, isHighLevel ? "高电平" : "低电平");

        // 更新状态记录
        _photoelectricSignalStates[photoelectricName] = isHighLevel;
    }
}
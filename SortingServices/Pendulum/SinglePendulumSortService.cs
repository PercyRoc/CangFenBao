using System.Text;
using Common.Models.Settings.Sort.PendulumSort;
using Common.Services.Settings;
using Serilog;
using SortingServices.Common;

namespace SortingServices.Pendulum;

/// <summary>
///     单光电单摆轮分拣服务实现
/// </summary>
internal class SinglePendulumSortService(ISettingsService settingsService) : BasePendulumSortService(settingsService)
{
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

        // 初始化摆轮状态
        PendulumStates["默认"] = new PendulumState();

        Log.Information("单光电单摆轮分拣服务初始化完成");
        return Task.CompletedTask;
    }

    public override Task StartAsync()
    {
        if (IsRunningFlag) return Task.CompletedTask;

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
                // 向触发光电发送启动命令
                if (TriggerClient != null && TriggerClient.IsConnected())
                {
                    var startCommand = GetCommandBytes(PendulumCommands.Module2.Start);
                    TriggerClient.Send(startCommand);
                    var resetLeftCommand = GetCommandBytes(PendulumCommands.Module2.ResetLeft);
                    var resetRightCommand = GetCommandBytes(PendulumCommands.Module2.ResetRight);
                    TriggerClient.Send(resetLeftCommand);
                    TriggerClient.Send(resetRightCommand);
                    Log.Debug("已发送启动命令到触发光电");
                }
                else
                {
                    Log.Warning("触发光电未连接，无法发送启动命令");
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
        }, CancellationTokenSource.Token);

        return Task.CompletedTask;
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

            // 向触发光电发送停止命令
            if (TriggerClient != null && TriggerClient.IsConnected())
                try
                {
                    // 发送左右回正指令
                    var resetLeftCommand = GetCommandBytes(PendulumCommands.Module2.ResetLeft);
                    var resetRightCommand = GetCommandBytes(PendulumCommands.Module2.ResetRight);
                    TriggerClient.Send(resetLeftCommand);
                    TriggerClient.Send(resetRightCommand);
                    Log.Debug("已发送左右回正命令到触发光电");

                    // 发送停止命令
                    var stopCommand = GetCommandBytes(PendulumCommands.Module2.Stop);
                    TriggerClient.Send(stopCommand);
                    Log.Debug("已发送停止命令到触发光电");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "向触发光电发送停止命令失败");
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

            Log.Information("单光电单摆轮分拣服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止单光电单摆轮分拣服务时发生错误");
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

            Log.Information("单光电单摆轮分拣服务配置已更新");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新单光电单摆轮分拣服务配置失败");
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

    /// <summary>
    ///     处理第二光电信号
    /// </summary>
    protected override void HandleSecondPhotoelectric(string data)
    {
        // 使用基类的匹配逻辑
        var package = MatchPackageForSorting("默认");
        if (package == null) return;

        // 执行分拣动作
        _ = ExecuteSortingAction(package, "默认");
    }

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

                // 如果服务正在运行，发送启动命令
                if (IsRunningFlag)
                {
                    var startCommand = GetCommandBytes(PendulumCommands.Module2.Start);
                    TriggerClient.Send(startCommand);
                    Log.Debug("重连后已发送启动命令到触发光电");
                }
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
        // 单摆轮只处理1、2号格口
        return targetSlot is 1 or 2;
    }

    protected override TriggerPhotoelectric GetPhotoelectricConfig(string photoelectricName)
    {
        // 单摆轮使用触发光电的配置
        return Configuration.TriggerPhotoelectric;
    }

    protected override TcpClientService? GetSortingClient(string photoelectricName)
    {
        return TriggerClient;
    }

    protected override string? GetPhotoelectricNameBySlot(int slot)
    {
        // 单摆轮只处理1、2号格口，使用默认光电
        return slot is 1 or 2 ? "默认" : null;
    }
}
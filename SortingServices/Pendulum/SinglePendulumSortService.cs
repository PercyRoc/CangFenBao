using System.Text;
using Common.Models.Settings.Sort.PendulumSort;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.TCP;
using Serilog;

namespace SortingServices.Pendulum;

/// <summary>
///     单光电单摆轮分拣服务实现
/// </summary>
public class SinglePendulumSortService(ISettingsService settingsService) : BasePendulumSortService(settingsService)
{
    private readonly ISettingsService _settingsService = settingsService;

    // 记录上次分拣信号时间
    private DateTime _lastSortingSignalTime = DateTime.MinValue;

    public override Task InitializeAsync(PendulumSortConfig configuration)
    {
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
        PendulumStates["默认"] = new PendulumState("默认");

        Log.Information("单光电单摆轮分拣服务初始化完成");
        return Task.CompletedTask;
    }

    public override Task StartAsync()
    {
        if (IsRunningFlag) return Task.CompletedTask;

        // 在启动服务前先发送启动命令
        if (TriggerClient != null && TriggerClient.IsConnected())
        {
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
        }
        else
        {
            Log.Warning("触发光电未连接，将在连接后自动发送启动命令");
        }

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
            // 先向触发光电发送停止命令，确保在改变服务状态前发送
            if (TriggerClient != null && TriggerClient.IsConnected())
            {
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
            }
            else
            {
                Log.Warning("触发光电未连接，无法发送停止命令");
            }

            // 停止主循环
            await CancellationTokenSource?.CancelAsync()!;
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

    private async void ProcessTriggerData(byte[] data)
    {
        try
        {
            var message = Encoding.ASCII.GetString(data);
            Log.Debug("收到触发光电数据: {Message}", message);

            // 使用基类的信号处理方法
            await HandlePhotoelectricSignalAsync(message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理触发光电数据时发生错误");
        }
    }

    /// <summary>
    ///     处理第二光电信号
    /// </summary>
    protected override async Task HandleSecondPhotoelectricAsync(string data)
    {
        // 重复信号过滤逻辑
        var now = DateTime.Now;
        var config = _settingsService.LoadSettings<PendulumSortConfig>();
        var filterMs = config.DuplicateSignalFilterMs;
        if (_lastSortingSignalTime != DateTime.MinValue)
        {
            var interval = (now - _lastSortingSignalTime).TotalMilliseconds;
            if (interval < filterMs)
            {
                Log.Warning("单摆轮分拣光电信号间隔 {Interval}ms 小于过滤阈值 {Threshold}ms，本次信号被忽略", interval, filterMs);
                return;
            }
        }
        _lastSortingSignalTime = now;
        // 使用基类的匹配逻辑
        var package = await MatchPackageForSorting("默认");
        if (package == null) return;
        // 执行分拣动作
        _ = ExecuteSortingAction(package, "默认");
    }

    protected override async Task ReconnectAsync()
    {
        try
        {
            // 检查触发光电配置是否有效
            if (string.IsNullOrEmpty(_settingsService.LoadSettings<PendulumSortConfig>().TriggerPhotoelectric
                    .IpAddress) ||
                _settingsService.LoadSettings<PendulumSortConfig>().TriggerPhotoelectric.Port == 0)
            {
                Log.Warning("触发光电未配置，跳过重连");
                return;
            }

            // 重新创建触发光电客户端
            TriggerClient = new TcpClientService(
                "触发光电",
                _settingsService.LoadSettings<PendulumSortConfig>().TriggerPhotoelectric.IpAddress,
                _settingsService.LoadSettings<PendulumSortConfig>().TriggerPhotoelectric.Port,
                ProcessTriggerData,
                connected => UpdateDeviceConnectionState("触发光电", connected)
            );
            await Task.Run(() => TriggerClient.Connect());

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
        return _settingsService.LoadSettings<PendulumSortConfig>().TriggerPhotoelectric;
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
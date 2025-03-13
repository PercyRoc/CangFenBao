using Common.Models.Settings.Weight;
using Common.Services.Settings;
using Common.Services.Ui;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DeviceService.DataSourceDevices.Weight;

/// <summary>
///     重量称启动服务
/// </summary>
public class WeightStartupService : IHostedService
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private SerialPortWeightService? _weightService;
    private WeightSettings? _currentSettings;

    /// <summary>
    ///     构造函数
    /// </summary>
    public WeightStartupService(
        INotificationService notificationService,
        ISettingsService settingsService)
    {
        _notificationService = notificationService;
        _settingsService = settingsService;

        // 订阅配置变更事件
        _settingsService.OnSettingsChanged<WeightSettings>(OnWeightSettingsChanged);
    }

    /// <summary>
    ///     启动服务
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("Starting weight scale service...");
            var weight = GetWeightService();

            // Load configuration
            Log.Debug("Loading weight scale configuration...");
            var config = _settingsService.LoadSettings<WeightSettings>();
            _currentSettings = config;

            // Update configuration
            Log.Debug("Updating weight scale configuration...");
            weight.UpdateConfiguration(config);

            // Start service
            if (!weight.Start())
            {
                const string message = "Failed to start weight scale service";
                Log.Warning(message);
                _notificationService.ShowError(message);
            }
            else
            {
                Log.Information("Weight scale service started successfully");
                _notificationService.ShowSuccess("Weight scale service started successfully");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动重量称服务时发生错误");
            _notificationService.ShowError(ex.Message);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     停止服务
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("正在停止重量称服务...");
            if (_weightService != null)
            {
                // 使用超时机制确保不会无限等待
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    timeoutCts.Token, cancellationToken);

                try
                {
                    // 先停止服务
                    _weightService.Stop();
                    // 释放资源
                    _weightService.Dispose();
                    _weightService = null;
                }
                catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
                {
                    Log.Warning("停止重量称服务超时");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "停止重量称服务时发生错误");
                }
            }

            Log.Information("重量称服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止重量称服务时发生错误");
        }
        finally
        {
            _initLock.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     获取重量称服务实例
    /// </summary>
    public SerialPortWeightService GetWeightService()
    {
        _initLock.Wait();
        try
        {
            return _weightService ??= new SerialPortWeightService();
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    ///     处理配置变更
    /// </summary>
    private void OnWeightSettingsChanged(WeightSettings settings)
    {
        try
        {
            Log.Information("检测到重量称配置变更，准备更新配置...");

            var weight = GetWeightService();

            // 检查串口参数是否发生变化
            var needRestart = _currentSettings == null ||
                              !AreSerialPortParamsEqual(_currentSettings.SerialPortParams, settings.SerialPortParams);

            _currentSettings = settings;

            if (needRestart)
            {
                Log.Information("串口参数发生变化，需要重启串口...");
                _notificationService.ShowSuccess("串口参数发生变化，正在重启串口...");
                weight.Stop();
                weight.UpdateConfiguration(settings);
                if (!weight.Start())
                {
                    Log.Warning("重启串口失败");
                    _notificationService.ShowError("重启串口失败");
                }
                else
                {
                    Log.Information("串口重启成功");
                }
            }
            else
            {
                Log.Information("仅更新配置参数...");
                weight.UpdateConfiguration(settings);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新重量称配置时发生错误");
            _notificationService.ShowError("更新重量称配置失败：" + ex.Message);
        }
    }

    /// <summary>
    ///     比较两个串口参数是否相同
    /// </summary>
    private static bool AreSerialPortParamsEqual(SerialPortParams? oldParams, SerialPortParams? newParams)
    {
        if (oldParams == null || newParams == null) return false;

        return oldParams.PortName == newParams.PortName &&
               oldParams.BaudRate == newParams.BaudRate &&
               oldParams.DataBits == newParams.DataBits &&
               oldParams.StopBits == newParams.StopBits &&
               oldParams.Parity == newParams.Parity;
    }
}
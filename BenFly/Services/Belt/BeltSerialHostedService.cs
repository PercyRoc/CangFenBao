using System.IO.Ports;
using Common.Services.Settings;
using Microsoft.Extensions.Hosting;
using BenFly.Models.Settings;
using Serilog;

namespace BenFly.Services.Belt;

/// <summary>
///     皮带串口托管服务
/// </summary>
internal class BeltSerialHostedService(
    IBeltSerialService serialService,
    ISettingsService settingsService)
    : IHostedService
{
    private readonly CancellationTokenSource _cts = new();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 加载串口设置
            var settings = settingsService.LoadSettings<BeltSettings>();

            // 如果没有配置串口或串口不存在，则不打开
            if (string.IsNullOrEmpty(settings.PortName))
            {
                Log.Warning("未配置串口");
                return Task.CompletedTask;
            }

            var availablePorts = SerialPort.GetPortNames();
            if (!availablePorts.Contains(settings.PortName))
            {
                Log.Warning("配置的串口 {PortName} 不存在。可用串口: {AvailablePorts}", 
                    settings.PortName, 
                    string.Join(", ", availablePorts));
                return Task.CompletedTask;
            }

            try
            {
                // 创建串口配置
                var serialPort = new SerialPort
                {
                    PortName = settings.PortName,
                    BaudRate = settings.BaudRate,
                    DataBits = settings.DataBits,
                    Parity = settings.Parity,
                    StopBits = settings.StopBits
                };

                // 打开串口
                serialService.Open(serialPort);
            }
            catch (InvalidOperationException ex) when (ex.InnerException is UnauthorizedAccessException)
            {
                Log.Error(ex, "无法访问串口 {PortName}，请以管理员身份运行程序或检查串口是否被占用", settings.PortName);
                // 不抛出异常，让服务继续运行
                return Task.CompletedTask;
            }

            // 监听设置变更
            settingsService.OnSettingsChanged<BeltSettings>(OnSettingsChanged);

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动串口服务时发生错误");
            throw;
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _cts.Cancel();
            serialService.Close();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止串口服务时发生错误");
            throw;
        }
    }

    private void OnSettingsChanged(BeltSettings settings)
    {
        try
        {
            var availablePorts = SerialPort.GetPortNames();
            if (!availablePorts.Contains(settings.PortName))
            {
                Log.Warning("串口 {PortName} 不存在。可用串口: {AvailablePorts}", 
                    settings.PortName, 
                    string.Join(", ", availablePorts));
                return;
            }

            // 创建新的串口配置
            var serialPort = new SerialPort
            {
                PortName = settings.PortName,
                BaudRate = settings.BaudRate,
                DataBits = settings.DataBits,
                Parity = settings.Parity,
                StopBits = settings.StopBits
            };

            try
            {
                // 重新打开串口
                serialService.Open(serialPort);
            }
            catch (InvalidOperationException ex) when (ex.InnerException is UnauthorizedAccessException)
            {
                Log.Error(ex, "无法访问串口 {PortName}，请以管理员身份运行程序或检查串口是否被占用", settings.PortName);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新串口设置时发生错误");
        }
    }
}
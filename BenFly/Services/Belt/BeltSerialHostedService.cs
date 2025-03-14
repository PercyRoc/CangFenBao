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
            if (string.IsNullOrEmpty(settings.PortName) ||
                !SerialPort.GetPortNames().Contains(settings.PortName))
            {
                Log.Warning("未配置串口或串口不存在");
                return Task.CompletedTask;
            }

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
            // 如果串口不存在，则不打开
            if (!SerialPort.GetPortNames().Contains(settings.PortName))
            {
                Log.Warning("串口 {PortName} 不存在", settings.PortName);
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

            // 重新打开串口
            serialService.Open(serialPort);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新串口设置时发生错误");
        }
    }
}
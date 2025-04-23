using System.ComponentModel;
using System.Text.Json.Serialization;
using DeviceService.DataSourceDevices.Weight;

namespace DeviceService.DataSourceDevices.Belt;

/// <summary>
/// 皮带串口参数
/// </summary>
public class BeltSerialParams : SerialPortParams // 继承自 Weight 目录下的 SerialPortParams
{
    /// <summary>
    /// 是否启用皮带
    /// </summary>
    [JsonPropertyName("isEnabled")]
    [DefaultValue(true)]
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 启动皮带命令
    /// </summary>
    [JsonPropertyName("startCommand")]
    public string StartCommand { get; set; } = "START_LB\r\n";

    /// <summary>
    /// 停止皮带命令
    /// </summary>
    [JsonPropertyName("stopCommand")]
    public string StopCommand { get; set; } = "STOP_LB\r\n";

    /// <summary>
    /// 复制皮带串口参数
    /// </summary>
    public BeltSerialParams Copy()
    {
        return new BeltSerialParams
        {
            IsEnabled = IsEnabled,
            PortName = PortName,
            BaudRate = BaudRate,
            DataBits = DataBits,
            StopBits = StopBits,
            Parity = Parity,
            StartCommand = StartCommand,
            StopCommand = StopCommand
        };
    }
} 
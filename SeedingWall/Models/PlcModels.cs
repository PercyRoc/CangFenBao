using System.Text.Json.Serialization;
using Common.Services.Settings;

namespace SeedingWall.Models;

/// <summary>
///     PLC设置
/// </summary>
[Configuration("PlcSettings")]
public class PlcSettings
{
    /// <summary>
    ///     服务器IP地址
    /// </summary>
    [JsonPropertyName("serverIp")]
    public string ServerIp { get; set; } = "127.0.0.1";

    /// <summary>
    ///     服务器端口
    /// </summary>
    [JsonPropertyName("serverPort")]
    public int ServerPort { get; set; } = 4600;
}

/// <summary>
///     PLC通信指令类型
/// </summary>
internal enum PlcCommandType
{
    /// <summary>
    ///     分拣指令（上位机->PLC）
    /// </summary>
    SortingCommand,

    /// <summary>
    ///     落格反馈（PLC->上位机）
    /// </summary>
    FeedbackCommand
}

/// <summary>
///     PLC通信指令
/// </summary>
public class PlcCommand
{
    /// <summary>
    ///     指令类型
    /// </summary>
    internal PlcCommandType CommandType { get; set; }

    /// <summary>
    ///     包裹号（1-100循环）
    /// </summary>
    internal int PackageNumber { get; set; }

    /// <summary>
    ///     格口号
    /// </summary>
    internal int SlotNumber { get; set; }

    /// <summary>
    ///     报文序号（1-9循环）
    /// </summary>
    internal int SequenceNumber { get; set; }

    /// <summary>
    ///     将指令转换为字符串
    /// </summary>
    /// <returns>指令字符串</returns>
    public override string ToString()
    {
        var header = CommandType == PlcCommandType.SortingCommand ? "[C" : "[O";
        return $"{header}{PackageNumber:D3}]{SlotNumber:D3},{SequenceNumber}";
    }

    /// <summary>
    ///     从字符串解析指令
    /// </summary>
    /// <param name="commandString">指令字符串</param>
    /// <returns>PLC指令对象</returns>
    internal static PlcCommand Parse(string commandString)
    {
        if (string.IsNullOrEmpty(commandString) || commandString.Length != 11)
            throw new ArgumentException("指令格式错误，长度必须为11字节", nameof(commandString));

        // 解析指令类型
        PlcCommandType commandType;
        if (commandString.StartsWith("[C", StringComparison.Ordinal))
            commandType = PlcCommandType.SortingCommand;
        else if (commandString.StartsWith("[O", StringComparison.Ordinal))
            commandType = PlcCommandType.FeedbackCommand;
        else
            throw new ArgumentException("指令格式错误，头字节必须为[C或[O", nameof(commandString));

        try
        {
            // 解析包裹号
            var packageNumber = int.Parse(commandString.Substring(2, 3));

            // 解析格口号
            var slotNumber = int.Parse(commandString.Substring(6, 3));

            // 解析报文序号
            var sequenceNumber = int.Parse(commandString.Substring(10, 1));

            return new PlcCommand
            {
                CommandType = commandType,
                PackageNumber = packageNumber,
                SlotNumber = slotNumber,
                SequenceNumber = sequenceNumber
            };
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"指令解析错误: {ex.Message}", nameof(commandString), ex);
        }
    }
}
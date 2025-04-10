namespace XinBeiYang.Models.Communication.JdWcs;

/// <summary>
/// 京东WCS设备状态枚举
/// </summary>
public enum JdDeviceStatus : sbyte
{
    /// <summary>
    /// 设备停机
    /// </summary>
    Stopped = 0,
    
    /// <summary>
    /// 设备运行中
    /// </summary>
    Running = 1,
    
    /// <summary>
    /// 设备故障
    /// </summary>
    Fault = 2
} 
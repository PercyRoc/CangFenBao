namespace XinBeiYang.Models.Communication.JdWcs;

/// <summary>
/// 京东WCS消息类型枚举
/// </summary>
public enum JdWcsMessageType : short
{
    /// <summary>
    /// 心跳消息
    /// </summary>
    Heartbeat = 1,
    
    /// <summary>
    /// 扫码上报
    /// </summary>
    ScanReport = 2,
    
    /// <summary>
    /// 分拣格口下发
    /// </summary>
    SortingCellAssign = 3,
    
    /// <summary>
    /// 分拣结果上报
    /// </summary>
    SortingResultReport = 4,
    
    /// <summary>
    /// 格口状态上报
    /// </summary>
    CellStatusReport = 5,
    
    /// <summary>
    /// 格口绑定RFID上报
    /// </summary>
    CellRfidBindingReport = 6,
    
    /// <summary>
    /// 设备运行状态及异常信息上报
    /// </summary>
    DeviceStatusReport = 7,
    
    /// <summary>
    /// 扫描包裹图片地址上报
    /// </summary>
    ImageUrlReport = 8,
    
    /// <summary>
    /// 设备调速查询
    /// </summary>
    SpeedQuery = 9,
    
    /// <summary>
    /// 设备调速参数下发
    /// </summary>
    SpeedAdjustment = 10,
    
    /// <summary>
    /// 格口状态控制
    /// </summary>
    CellStatusControl = 11,
    
    /// <summary>
    /// 设备模式调整上报
    /// </summary>
    DeviceModeReport = 12,
    
    /// <summary>
    /// 设备配置信息上报
    /// </summary>
    DeviceConfigReport = 101,
    
    /// <summary>
    /// 格口RFID识读设备状态上报
    /// </summary>
    RfidReaderStatusReport = 102,
    
    /// <summary>
    /// 模块通讯状态上报
    /// </summary>
    ModuleCommunicationReport = 103,
    
    // ACK确认消息
    
    /// <summary>
    /// 心跳消息ACK确认
    /// </summary>
    HeartbeatAck = 1001,
    
    /// <summary>
    /// 扫码上报ACK确认
    /// </summary>
    ScanReportAck = 1002,
    
    /// <summary>
    /// 分拣格口下发ACK确认
    /// </summary>
    SortingCellAssignAck = 1003,
    
    /// <summary>
    /// 分拣结果上报ACK确认
    /// </summary>
    SortingResultReportAck = 1004,
    
    /// <summary>
    /// 格口状态上报ACK确认
    /// </summary>
    CellStatusReportAck = 1005,
    
    /// <summary>
    /// 格口绑定RFID上报ACK确认
    /// </summary>
    CellRfidBindingReportAck = 1006,
    
    /// <summary>
    /// 扫描包裹图片地址上报ACK确认
    /// </summary>
    ImageUrlReportAck = 1008,
    
    /// <summary>
    /// 设备调速查询ACK确认
    /// </summary>
    SpeedQueryAck = 1009,
    
    /// <summary>
    /// 设备调速参数下发ACK确认
    /// </summary>
    SpeedAdjustmentAck = 1010,
    
    /// <summary>
    /// 格口状态控制ACK确认
    /// </summary>
    CellStatusControlAck = 1011,
    
    /// <summary>
    /// 设备模式调整上报ACK确认
    /// </summary>
    DeviceModeReportAck = 1012,
    
    /// <summary>
    /// 通用ACK确认
    /// </summary>
    Ack = 99
} 
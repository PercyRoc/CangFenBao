using System.ComponentModel;

namespace DeviceService.DataSourceDevices.Camera.Models.Camera.Enums;

/// <summary>
///     相机厂商
/// </summary>
public enum CameraManufacturer
{
    /// <summary>
    ///     华睿
    /// </summary>
    [Description("华睿")]
    HuaRay,

    /// <summary>
    ///     海康
    /// </summary>
    [Description("海康")]
    Hikvision,

    /// <summary>
    ///     TCP相机
    /// </summary>
    [Description("TCP")]
    Tcp
}

/// <summary>
///     相机类型
/// </summary>
public enum CameraType
{
    /// <summary>
    ///     工业相机
    /// </summary>
    [Description("工业相机")]
    Industrial,

    /// <summary>
    ///     智能相机
    /// </summary>
    [Description("智能相机")]
    Smart,

    /// <summary>
    ///     TCP相机
    /// </summary>
    [Description("TCP相机")]
    Tcp
}

public enum CameraStatus
{
    [Description("离线")] Offline,

    [Description("在线")] Online
}
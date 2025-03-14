using System.ComponentModel;

namespace DeviceService.DataSourceDevices.Camera.Models.Camera.Enums;

/// <summary>
///     相机厂商
/// </summary>
public enum CameraManufacturer
{
    /// <summary>
    ///     大华
    /// </summary>
    Dahua,

    /// <summary>
    ///     海康
    /// </summary>
    Hikvision,

    /// <summary>
    ///     TCP相机
    /// </summary>
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
    Industrial,

    /// <summary>
    ///     智能相机
    /// </summary>
    Smart,

    /// <summary>
    ///     TCP相机
    /// </summary>
    Tcp
}

public enum CameraStatus
{
    [Description("离线")] Offline,

    [Description("在线")] Online
}
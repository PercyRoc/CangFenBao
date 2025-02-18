using System.ComponentModel;

namespace CommonLibrary.Models.Settings.Camera.Enums;

public enum CameraManufacturer
{
    [Description("海康")] Hikvision,

    [Description("大华")] Dahua
}

public enum CameraType
{
    [Description("智能相机")] Smart,

    [Description("工业相机")] 工业的
}

public enum CameraStatus
{
    [Description("离线")] Offline,

    [Description("在线")] Online
}
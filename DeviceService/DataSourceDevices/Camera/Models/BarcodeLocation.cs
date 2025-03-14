namespace DeviceService.DataSourceDevices.Camera.Models;

/// <summary>
///     条码位置信息
/// </summary>
public class BarcodeLocation
{
    /// <summary>
    ///     条码内容
    /// </summary>
    internal string Code { get; set; } = string.Empty;

    /// <summary>
    ///     条码类型
    /// </summary>
    public string Type { get; set; } = string.Empty;
}
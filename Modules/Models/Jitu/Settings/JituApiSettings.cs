using Common.Services.Settings;

namespace ShanghaiModuleBelt.Models.Jitu.Settings;

[Configuration("JituApiSettings")]
public class JituApiSettings
{
    /// <summary>
    ///     极兔OpScan接口地址
    /// </summary>
    public string OpScanUrl { get; set; } = "http://127.0.0.1:8080/OpScan";

    /// <summary>
    ///     设备编号
    /// </summary>
    public string DeviceCode { get; set; } = "JC0001";

    /// <summary>
    ///     设备名称
    /// </summary>
    public string DeviceName { get; set; } = "测试机器";

    /// <summary>
    ///     条码前缀（分号分隔）
    /// </summary>
    public string BarcodePrefixes { get; set; } = "";
}
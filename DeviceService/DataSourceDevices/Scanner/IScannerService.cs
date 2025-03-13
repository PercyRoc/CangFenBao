namespace DeviceService.DataSourceDevices.Scanner;

/// <summary>
///     扫码枪服务接口
/// </summary>
public interface IScannerService : IDisposable
{
    /// <summary>
    ///     扫码完成事件
    /// </summary>
    event EventHandler<string> BarcodeScanned;

    /// <summary>
    ///     启动扫码服务
    /// </summary>
    /// <returns>是否成功启动</returns>
    bool Start();

    /// <summary>
    ///     停止扫码服务
    /// </summary>
    void Stop();
}
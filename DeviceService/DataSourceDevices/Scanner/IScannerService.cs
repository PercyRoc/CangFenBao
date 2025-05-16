namespace DeviceService.DataSourceDevices.Scanner;

/// <summary>
///     扫码枪服务接口
/// </summary>
public interface IScannerService : IDisposable
{
    /// <summary>
    ///     开始监听扫码枪
    /// </summary>
    /// <returns>是否成功启动</returns>
    bool Start();

    /// <summary>
    ///     停止监听扫码枪
    /// </summary>
    void Stop();

    /// <summary>
    ///     条码数据流
    /// </summary>
    IObservable<string> BarcodeStream { get; }
}
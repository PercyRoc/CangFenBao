namespace DeviceService.DataSourceDevices.Scanner;

/// <summary>
///     扫码枪服务接口
/// </summary>
public interface IScannerService : IDisposable
{
    /// <summary>
    ///     条码数据流
    /// </summary>
    IObservable<string> BarcodeStream { get; }

    /// <summary>
    ///     获取或设置是否拦截所有扫码枪输入，防止字符进入输入框
    ///     设置为true表示阻止扫码枪字符进入文本框等输入控件
    ///     设置为false表示允许扫码枪字符同时进入文本框
    /// </summary>
    bool InterceptAllInput { get; set; }
    /// <summary>
    ///     开始监听扫码枪
    /// </summary>
    /// <returns>是否成功启动</returns>
    bool Start();

    /// <summary>
    ///     停止监听扫码枪
    /// </summary>
    void Stop();
}
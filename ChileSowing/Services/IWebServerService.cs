namespace ChileSowing.Services;

/// <summary>
/// Web服务器服务接口
/// </summary>
public interface IWebServerService
{
    /// <summary>
    /// 启动Web服务器
    /// </summary>
    /// <returns></returns>
    Task StartAsync();

    /// <summary>
    /// 停止Web服务器
    /// </summary>
    /// <returns></returns>
    Task StopAsync();

    /// <summary>
    /// 是否正在运行
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// 服务器URL
    /// </summary>
    string? ServerUrl { get; }
} 
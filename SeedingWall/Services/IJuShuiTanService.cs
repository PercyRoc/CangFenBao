using SeedingWall.Models;

namespace SeedingWall.Services;

/// <summary>
///     聚水潭服务接口
/// </summary>
public interface IJuShuiTanService : IDisposable
{
    /// <summary>
    ///     是否已连接
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    ///     WebSocket连接状态变更事件
    /// </summary>
    event Action<bool> ConnectionStatusChanged;

    /// <summary>
    ///     收到消息事件
    /// </summary>
    event Action<string> MessageReceived;

    /// <summary>
    ///     错误事件
    /// </summary>
    event Action<Exception> ErrorOccurred;

    /// <summary>
    ///     连接到WebSocket服务器
    /// </summary>
    /// <param name="url">WebSocket服务器地址</param>
    /// <returns>连接结果</returns>
    Task<bool> ConnectAsync(string url);

    /// <summary>
    ///     断开WebSocket连接
    /// </summary>
    void Disconnect();

    /// <summary>
    ///     发送播种验货请求
    /// </summary>
    /// <param name="request">播种验货请求</param>
    /// <returns>发送结果</returns>
    Task<bool> SendSeedingVerificationAsync(SeedingVerificationRequest request);
}
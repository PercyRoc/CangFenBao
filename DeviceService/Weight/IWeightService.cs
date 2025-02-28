using CommonLibrary.Models.Settings.Weight;

namespace DeviceService.Weight;

/// <summary>
///     重量称服务接口
/// </summary>
public interface IWeightService : IDisposable, IAsyncDisposable
{
    /// <summary>
    ///     是否已连接
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    ///     连接状态变更事件
    /// </summary>
    event Action<string, bool>? ConnectionChanged;

    /// <summary>
    ///     启动服务
    /// </summary>
    Task<bool> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     停止服务
    /// </summary>
    Task StopAsync();

    /// <summary>
    ///     更新配置
    /// </summary>
    Task UpdateConfigurationAsync(WeightSettings config);

    /// <summary>
    ///     查找最近的重量数据
    /// </summary>
    /// <param name="targetTime">目标时间</param>
    /// <returns>找到的重量数据（克）</returns>
    double? FindNearestWeight(DateTime targetTime);
}
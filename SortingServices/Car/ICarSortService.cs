using Common.Models.Package;

namespace SortingServices.Car;

/// <summary>
/// 小车分拣服务接口
/// </summary>
public interface ICarSortService : IDisposable
{
    /// <summary>
    /// 初始化
    /// </summary>
    /// <returns>初始化结果</returns>
    Task<bool> InitializeAsync();

    /// <summary>
    /// 开始分拣服务
    /// </summary>
    /// <returns>开始服务结果</returns>
    Task<bool> StartAsync();

    /// <summary>
    /// 停止分拣服务
    /// </summary>
    /// <returns>停止服务结果</returns>
    Task<bool> StopAsync();

    /// <summary>
    /// 处理包裹分拣
    /// </summary>
    /// <param name="package">待分拣的包裹</param>
    /// <returns>分拣结果</returns>
    Task<bool> ProcessPackageSortingAsync(PackageInfo package);

    /// <summary>
    /// 重置小车
    /// </summary>
    /// <returns>重置结果</returns>
    Task<bool> ResetCarAsync();

    /// <summary>
    /// 连接状态
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 服务状态
    /// </summary>
    bool IsRunning { get; }
} 
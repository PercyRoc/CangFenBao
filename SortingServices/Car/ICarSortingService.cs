using Common.Models.Package;

namespace SortingServices.Car
{
    /// <summary>
    /// 小车分拣服务接口
    /// </summary>
    public interface ICarSortingService : IAsyncDisposable
    {
        /// <summary>
        /// 初始化服务，加载配置并连接串口
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>初始化是否成功</returns>
        Task<bool> InitializeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 根据包裹信息发送分拣命令
        /// </summary>
        /// <param name="package">包裹信息，主要使用其 ChuteNumber</param>
        /// <returns>发送是否成功启动（不保证所有命令都成功）</returns>
        Task<bool> SendCommandForPackageAsync(PackageInfo package);

        /// <summary>
        /// 根据格口号发送分拣命令序列
        /// </summary>
        /// <param name="chuteNumber">目标格口号</param>
        /// <returns>发送是否成功启动（不保证所有命令都成功）</returns>
        Task<bool> SendCommandForChuteAsync(int chuteNumber);
        
        /// <summary>
        /// 获取当前串口连接状态
        /// </summary>
        bool IsConnected { get; }
    }
} 
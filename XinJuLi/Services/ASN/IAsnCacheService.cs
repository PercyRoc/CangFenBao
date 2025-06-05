using XinJuLi.Models.ASN;

namespace XinJuLi.Services.ASN
{
    /// <summary>
    /// ASN单缓存服务接口
    /// </summary>
    public interface IAsnCacheService
    {
        /// <summary>
        /// 添加ASN单到缓存
        /// </summary>
        /// <param name="asnOrderInfo">ASN单信息</param>
        void AddAsnOrder(AsnOrderInfo asnOrderInfo);

        /// <summary>
        /// 获取所有缓存的ASN单
        /// </summary>
        /// <returns>ASN单列表</returns>
        List<AsnOrderInfo> GetAllAsnOrders();

        /// <summary>
        /// 根据订单号获取ASN单
        /// </summary>
        /// <param name="orderCode">订单号</param>
        /// <returns>ASN单信息，如果不存在则返回null</returns>
        AsnOrderInfo? GetAsnOrder(string orderCode);

        /// <summary>
        /// 移除指定的ASN单
        /// </summary>
        /// <param name="orderCode">订单号</param>
        /// <returns>是否移除成功</returns>
        bool RemoveAsnOrder(string orderCode);

        /// <summary>
        /// 清空所有缓存的ASN单
        /// </summary>
        void ClearAll();

        /// <summary>
        /// 获取缓存的ASN单数量
        /// </summary>
        int Count { get; }

        /// <summary>
        /// ASN单缓存变更事件
        /// </summary>
        event EventHandler<AsnCacheChangedEventArgs>? CacheChanged;
    }

    /// <summary>
    /// ASN缓存变更事件参数
    /// </summary>
    public class AsnCacheChangedEventArgs : EventArgs
    {
        public string Action { get; set; } = string.Empty; // Added, Removed, Cleared
        public AsnOrderInfo? AsnOrderInfo { get; set; }
    }
} 
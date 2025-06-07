using XinJuLi.Models.ASN;

namespace XinJuLi.Services.ASN
{
    /// <summary>
    /// ASN单存储服务接口
    /// </summary>
    public interface IAsnStorageService
    {
        /// <summary>
        /// 保存ASN单
        /// </summary>
        /// <param name="asnOrder">ASN单信息</param>
        void SaveAsnOrder(AsnOrderInfo asnOrder);

        /// <summary>
        /// 获取所有保存的ASN单
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
        /// 删除ASN单
        /// </summary>
        /// <param name="orderCode">订单号</param>
        /// <returns>是否删除成功</returns>
        bool DeleteAsnOrder(string orderCode);
    }
} 
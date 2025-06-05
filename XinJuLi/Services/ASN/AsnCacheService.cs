using System.Collections.Concurrent;
using Serilog;
using XinJuLi.Models.ASN;

namespace XinJuLi.Services.ASN
{
    /// <summary>
    /// ASN单缓存服务实现
    /// </summary>
    public class AsnCacheService : IAsnCacheService
    {
        private readonly ConcurrentDictionary<string, AsnOrderInfo> _asnCache = new();

        /// <summary>
        /// 获取缓存的ASN单数量
        /// </summary>
        public int Count => _asnCache.Count;

        /// <summary>
        /// ASN单缓存变更事件
        /// </summary>
        public event EventHandler<AsnCacheChangedEventArgs>? CacheChanged;

        /// <summary>
        /// 添加ASN单到缓存
        /// </summary>
        /// <param name="asnOrderInfo">ASN单信息</param>
        public void AddAsnOrder(AsnOrderInfo asnOrderInfo)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(asnOrderInfo.OrderCode))
                {
                    Log.Warning("尝试添加空订单号的ASN单到缓存");
                    return;
                }

                _asnCache.AddOrUpdate(asnOrderInfo.OrderCode, asnOrderInfo, (key, oldValue) => asnOrderInfo);

                Log.Information("ASN单已添加到缓存: {OrderCode}, 车牌: {CarCode}, 货品数量: {ItemsCount}",
                    asnOrderInfo.OrderCode, asnOrderInfo.CarCode, asnOrderInfo.Items.Count);

                // 触发缓存变更事件
                CacheChanged?.Invoke(this, new AsnCacheChangedEventArgs
                {
                    Action = "Added",
                    AsnOrderInfo = asnOrderInfo
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "添加ASN单到缓存时发生错误: {OrderCode}", asnOrderInfo.OrderCode);
            }
        }

        /// <summary>
        /// 获取所有缓存的ASN单
        /// </summary>
        /// <returns>ASN单列表</returns>
        public List<AsnOrderInfo> GetAllAsnOrders()
        {
            try
            {
                return _asnCache.Values.OrderByDescending(x => x.OrderCode).ToList();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "获取所有ASN单时发生错误");
                return new List<AsnOrderInfo>();
            }
        }

        /// <summary>
        /// 根据订单号获取ASN单
        /// </summary>
        /// <param name="orderCode">订单号</param>
        /// <returns>ASN单信息，如果不存在则返回null</returns>
        public AsnOrderInfo? GetAsnOrder(string orderCode)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(orderCode))
                    return null;

                return _asnCache.TryGetValue(orderCode, out var asnOrderInfo) ? asnOrderInfo : null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "获取ASN单时发生错误: {OrderCode}", orderCode);
                return null;
            }
        }

        /// <summary>
        /// 移除指定的ASN单
        /// </summary>
        /// <param name="orderCode">订单号</param>
        /// <returns>是否移除成功</returns>
        public bool RemoveAsnOrder(string orderCode)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(orderCode))
                    return false;

                var removed = _asnCache.TryRemove(orderCode, out var removedOrder);
                
                if (removed && removedOrder != null)
                {
                    Log.Information("ASN单已从缓存中移除: {OrderCode}", orderCode);

                    // 触发缓存变更事件
                    CacheChanged?.Invoke(this, new AsnCacheChangedEventArgs
                    {
                        Action = "Removed",
                        AsnOrderInfo = removedOrder
                    });
                }

                return removed;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "移除ASN单时发生错误: {OrderCode}", orderCode);
                return false;
            }
        }

        /// <summary>
        /// 清空所有缓存的ASN单
        /// </summary>
        public void ClearAll()
        {
            try
            {
                var count = _asnCache.Count;
                _asnCache.Clear();

                Log.Information("已清空所有ASN单缓存，共清理{Count}个订单", count);

                // 触发缓存变更事件
                CacheChanged?.Invoke(this, new AsnCacheChangedEventArgs
                {
                    Action = "Cleared",
                    AsnOrderInfo = null
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "清空ASN单缓存时发生错误");
            }
        }
    }
} 
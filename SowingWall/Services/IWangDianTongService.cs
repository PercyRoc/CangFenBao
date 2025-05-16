using SowingWall.Models.WangDianTong.Api;
using System.Threading.Tasks;

namespace SowingWall.Services
{
    public interface IWangDianTongService
    {
        /// <summary>
        /// 获取分拣单任务接口
        /// </summary>
        /// <param name="request">请求参数</param>
        /// <returns>API 响应</returns>
        Task<PickOrderTaskGetResponse?> GetPickOrderTaskAsync(PickOrderTaskGetRequest request);

        /// <summary>
        /// 修改分拣单状态接口
        /// </summary>
        /// <param name="request">请求参数</param>
        /// <returns>API 响应 (通用基础响应)</returns>
        Task<WdtApiResponseBase?> UpdatePickOrderStatusAsync(PickOrderStatusUpdateRequest request);
    }
} 
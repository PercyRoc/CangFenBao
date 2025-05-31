using System.Threading.Tasks;

namespace Server.YouYu.Services
{
    public interface IYouYuService
    {
        /// <summary>
        ///     异步获取段码信息。
        /// </summary>
        /// <param name="barcode">条码信息。</param>
        /// <returns>成功时返回格口号，否则返回 null。</returns>
        Task<string?> GetSegmentCodeAsync(string barcode);
    }
} 
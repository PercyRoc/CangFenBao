using XinJuLi.Models.ASN;

namespace XinJuLi.Services.ASN
{
    /// <summary>
    /// ASN服务接口
    /// </summary>
    public interface IAsnService
    {
        /// <summary>
        /// 处理ASN单数据
        /// </summary>
        /// <param name="asnInfo">ASN单信息</param>
        /// <returns>处理结果</returns>
        Response ProcessAsnOrderInfo(AsnOrderInfo asnInfo);

        /// <summary>
        /// 处理扫码复核请求
        /// </summary>
        /// <param name="request">复核请求</param>
        /// <returns>处理结果</returns>
        Task<Response> ProcessMaterialReview(MaterialReviewRequest request);
    }
}
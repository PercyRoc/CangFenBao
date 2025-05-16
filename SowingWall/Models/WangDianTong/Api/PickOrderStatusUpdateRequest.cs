using System.Text.Json.Serialization;

namespace SowingWall.Models.WangDianTong.Api
{
    public class PickOrderStatusUpdateRequest
    {
        /// <summary>
        /// 分拣单号
        /// </summary>
        [JsonPropertyName("pick_no")]
        public string PickNo { get; set; } = string.Empty;

        /// <summary>
        /// 分拣单状态（传数字编号）
        /// 30 拣货完成，先拣后分传该值
        /// 35 分货中
        /// 40 分拣完成，边拣边分传该值
        /// </summary>
        [JsonPropertyName("status")]
        public int Status { get; set; }
    }
} 
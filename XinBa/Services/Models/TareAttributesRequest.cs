using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace XinBa.Services.Models
{
    /// <summary>
    /// Tare Attributes API 请求模型
    /// 用于提交包裹测量属性（尺寸、重量等）到 Wildberries API
    /// </summary>
    public class TareAttributesRequest
    {
        /// <summary>
        /// 仓库办公室ID - Shelepanovo 仓库硬编码为 300684
        /// </summary>
        [JsonPropertyName("office_id")]
        [Required]
        public long OfficeId { get; set; } = 300684;

        /// <summary>
        /// 包装箱上的二维码或条形码值
        /// </summary>
        [JsonPropertyName("tare_sticker")]
        [Required]
        public string TareSticker { get; set; } = string.Empty;

        /// <summary>
        /// 测量机器的地点ID - Shelepanovo 机器硬编码为 943626653
        /// </summary>
        [JsonPropertyName("place_id")]
        [Required]
        public long PlaceId { get; set; } = 943626653;

        /// <summary>
        /// 长度，单位毫米 (mm)
        /// </summary>
        [JsonPropertyName("size_a_mm")]
        [Required]
        [Range(1, int.MaxValue, ErrorMessage = "Length must be greater than 0")]
        public long SizeAMm { get; set; }

        /// <summary>
        /// 宽度，单位毫米 (mm)
        /// </summary>
        [JsonPropertyName("size_b_mm")]
        [Required]
        [Range(1, int.MaxValue, ErrorMessage = "Width must be greater than 0")]
        public long SizeBMm { get; set; }

        /// <summary>
        /// 高度，单位毫米 (mm)
        /// </summary>
        [JsonPropertyName("size_c_mm")]
        [Required]
        [Range(1, int.MaxValue, ErrorMessage = "Height must be greater than 0")]
        public long SizeCMm { get; set; }

        /// <summary>
        /// 体积，必须是长、宽、高三者相乘的结果，单位立方毫米 (mm³)
        /// </summary>
        [JsonPropertyName("volume_mm")]
        [Required]
        public int VolumeMm { get; set; }

        /// <summary>
        /// 包装箱重量，单位克 (g)
        /// </summary>
        [JsonPropertyName("weight_g")]
        [Required]
        [Range(1, int.MaxValue, ErrorMessage = "Weight must be greater than 0")]
        public int WeightG { get; set; }

        /// <summary>
        /// 验证并自动计算体积
        /// </summary>
        public void CalculateVolume()
        {
            VolumeMm = (int)(SizeAMm * SizeBMm * SizeCMm);
        }
    }
} 
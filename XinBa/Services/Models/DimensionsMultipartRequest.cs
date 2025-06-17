using System.Text.Json.Serialization;

namespace XinBa.Services.Models
{
    /// <summary>
    /// 商品尺寸提交请求模型，匹配OpenAPI文档定义
    /// </summary>
    public class DimensionsMultipartRequest
    {
        /// <summary>
        /// WB商品条码
        /// </summary>
        [JsonPropertyName("goods_sticker")]
        public string GoodsSticker { get; set; } = string.Empty;

        /// <summary>
        /// 商品高度（毫米）
        /// </summary>
        [JsonPropertyName("height")]
        public int Height { get; set; }

        /// <summary>
        /// 商品长度（毫米）
        /// </summary>
        [JsonPropertyName("length")]
        public int Length { get; set; }

        /// <summary>
        /// 商品宽度（毫米）
        /// </summary>
        [JsonPropertyName("width")]
        public int Width { get; set; }

        /// <summary>
        /// 商品重量（毫克）
        /// </summary>
        [JsonPropertyName("weight")]
        public int Weight { get; set; }

        /// <summary>
        /// 商品图片数据列表
        /// </summary>
        [JsonPropertyName("photos")]
        public IEnumerable<byte[]>? Photos { get; set; }
    }
} 
using System.Text.Json.Serialization;

namespace XinJuLi.Models.ASN
{
    /// <summary>
    /// ASN订单信息模型
    /// </summary>
    public class AsnOrderInfo
    {
        /// <summary>
        /// 系统编码
        /// </summary>
        [JsonPropertyName("systemCode")]
        public string SystemCode { get; set; } = string.Empty;

        /// <summary>
        /// 仓库编码
        /// </summary>
        [JsonPropertyName("houseCode")]
        public string HouseCode { get; set; } = string.Empty;

        /// <summary>
        /// ASN单编码
        /// </summary>
        [JsonPropertyName("orderCode")]
        public string OrderCode { get; set; } = string.Empty;

        /// <summary>
        /// ASN单名称
        /// </summary>
        [JsonPropertyName("orderName")]
        public string OrderName { get; set; } = string.Empty;

        /// <summary>
        /// ASN单类型
        /// </summary>
        [JsonPropertyName("orderType")]
        public string OrderType { get; set; } = string.Empty;

        /// <summary>
        /// 车牌号
        /// </summary>
        [JsonPropertyName("carCode")]
        public string CarCode { get; set; } = string.Empty;

        /// <summary>
        /// 备注
        /// </summary>
        [JsonPropertyName("remark")]
        public string Remark { get; set; } = string.Empty;

        /// <summary>
        /// 货物明细
        /// </summary>
        [JsonPropertyName("items")]
        public List<AsnOrderItem> Items { get; set; } = [];

        /// <summary>
        /// 扩展项
        /// </summary>
        [JsonPropertyName("extra")]
        public Dictionary<string, object>? Extra { get; set; }

        /// <summary>
        /// 是否是新收到的ASN单（用于UI高亮，不参与JSON序列化）
        /// </summary>
        [JsonIgnore]
        public bool IsNewReceived { get; set; }

        /// <summary>
        /// 是否在UI中显示（用于过滤，不参与JSON序列化）
        /// </summary>
        [JsonIgnore]
        public bool IsVisible { get; set; } = true;
    }

    /// <summary>
    /// ASN订单项
    /// </summary>
    public class AsnOrderItem
    {
        /// <summary>
        /// 货物条码
        /// </summary>
        [JsonPropertyName("itemCode")]
        public string ItemCode { get; set; } = string.Empty;

        /// <summary>
        /// 货物名称
        /// </summary>
        [JsonPropertyName("itemName")]
        public string ItemName { get; set; } = string.Empty;

        /// <summary>
        /// 货物描述
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// 货物数量
        /// </summary>
        [JsonPropertyName("quantity")]
        public decimal Quantity { get; set; }

        /// <summary>
        /// 货物单位
        /// </summary>
        [JsonPropertyName("unit")]
        public string Unit { get; set; } = string.Empty;

        /// <summary>
        /// 单位重量
        /// </summary>
        [JsonPropertyName("weight")]
        public decimal? Weight { get; set; }

        /// <summary>
        /// SKU代码
        /// </summary>
        [JsonPropertyName("skuCode")]
        public string SkuCode { get; set; } = string.Empty;

        /// <summary>
        /// SKU名称
        /// </summary>
        [JsonPropertyName("skuName")]
        public string SkuName { get; set; } = string.Empty;
    }
} 
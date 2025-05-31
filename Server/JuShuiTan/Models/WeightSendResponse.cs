using System.Text.Json.Serialization;

namespace Server.JuShuiTan.Models
{
    public class WeightSendResponse
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("msg")]
        public string? Message { get; set; }

        [JsonPropertyName("datas")]
        public List<WeightSendData>? Datas { get; set; }

        [JsonPropertyName("issuccess")]
        public bool IsSuccess { get; set; }

        [JsonPropertyName("requestId")]
        public string? RequestId { get; set; }
    }

    public class WeightSendData
    {
        [JsonPropertyName("skus")]
        public string? Skus { get; set; }

        [JsonPropertyName("weight")]
        public double? Weight { get; set; }

        [JsonPropertyName("lc_id")]
        public string? LcId { get; set; }

        [JsonPropertyName("l_id")]
        public string? LId { get; set; }

        [JsonPropertyName("logistics_company")]
        public string? LogisticsCompany { get; set; }

        [JsonPropertyName("receiver_state")]
        public string? ReceiverState { get; set; }

        [JsonPropertyName("receiver_city")]
        public string? ReceiverCity { get; set; }

        [JsonPropertyName("receiver_district")]
        public string? ReceiverDistrict { get; set; }

        [JsonPropertyName("cb_lc_id")]
        public string? CbLcId { get; set; }

        [JsonPropertyName("cb_l_id")]
        public string? CbLId { get; set; }

        [JsonPropertyName("cb_logistics_company")]
        public string? CbLogisticsCompany { get; set; }

        [JsonPropertyName("is_success")]
        public bool IsSuccess { get; set; }

        [JsonPropertyName("msg")]
        public string? Msg { get; set; }
    }
} 
using System.Text.Json.Serialization;

namespace SortingServices.Servers.Services.JuShuiTan;

/// <summary>
///     聚水潭称重发货响应模型
/// </summary>
public class WeightSendResponse
{
    /// <summary>
    ///     错误码
    /// </summary>
    [JsonPropertyName("code")]
    public int Code { get; set; }

    /// <summary>
    ///     错误描述
    /// </summary>
    [JsonPropertyName("msg")]
    public string Message { get; set; } = null!;

    /// <summary>
    ///     数据集合
    /// </summary>
    [JsonPropertyName("data")]
    public WeightSendResponseData Data { get; set; } = new();
}

/// <summary>
///     称重发货响应数据
/// </summary>
public class WeightSendResponseData
{
    /// <summary>
    ///     数据集合
    /// </summary>
    [JsonPropertyName("datas")]
    public List<WeightSendResponseItem> Items { get; set; } = [];
}

/// <summary>
///     称重发货响应项
/// </summary>
public class WeightSendResponseItem
{
    /// <summary>
    ///     错误码
    /// </summary>
    [JsonPropertyName("code")]
    public int Code { get; set; }

    /// <summary>
    ///     错误描述
    /// </summary>
    [JsonPropertyName("msg")]
    public string Message { get; set; } = null!;

    /// <summary>
    ///     预估重量
    /// </summary>
    [JsonPropertyName("weight")]
    public decimal Weight { get; set; }

    /// <summary>
    ///     快递公司编码
    /// </summary>
    [JsonPropertyName("lc_id")]
    public string LogisticsCompanyId { get; set; } = null!;

    /// <summary>
    ///     快递单号
    /// </summary>
    [JsonPropertyName("l_id")]
    public string LogisticsId { get; set; } = null!;

    /// <summary>
    ///     物流公司
    /// </summary>
    [JsonPropertyName("logistics_company")]
    public string LogisticsCompany { get; set; } = null!;

    /// <summary>
    ///     省
    /// </summary>
    [JsonPropertyName("receiver_state")]
    public string ReceiverState { get; set; } = null!;

    /// <summary>
    ///     市
    /// </summary>
    [JsonPropertyName("receiver_city")]
    public string ReceiverCity { get; set; } = null!;

    /// <summary>
    ///     区
    /// </summary>
    [JsonPropertyName("receiver_district")]
    public string ReceiverDistrict { get; set; } = null!;
}
using System.Text.Json.Serialization;

namespace ZtCloudWarehous.Models;

/// <summary>
///     称重请求模型
/// </summary>
public class WeighingRequest
{
    // 公共参数
    [JsonPropertyName("api")]
    public string Api { get; set; } = string.Empty;

    [JsonPropertyName("customerId")]
    public string CustomerId { get; set; } = string.Empty;

    [JsonPropertyName("appkey")]
    public string AppKey { get; set; } = string.Empty;

    [JsonPropertyName("sig")]
    public string Sign { get; set; } = string.Empty;

    // 业务参数
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("warehouseCode")]
    public string WarehouseCode { get; set; } = string.Empty;

    [JsonPropertyName("waybillCode")]
    public string WaybillCode { get; set; } = string.Empty;

    [JsonPropertyName("packagingMaterialCode")]
    public string PackagingMaterialCode { get; set; } = string.Empty;

    [JsonPropertyName("actualWeight")]
    public decimal ActualWeight { get; set; }

    [JsonPropertyName("actualVolume")]
    public decimal ActualVolume { get; set; }

    [JsonPropertyName("weighingEquipment")]
    public string WeighingEquipment { get; set; } = "0";

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("userRealName")]
    public string UserRealName { get; set; } = string.Empty;
}

/// <summary>
///     称重响应模型
/// </summary>
public class WeighingResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("msg")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("result")]
    public object? Result { get; set; }
}

/// <summary>
///     新称重接口请求模型
/// </summary>
public class NewWeighingRequest
{
    /// <summary>
    ///     运单号
    /// </summary>
    [JsonPropertyName("waybillCode")]
    public string WaybillCode { get; set; } = string.Empty;

    /// <summary>
    ///     重量
    /// </summary>
    [JsonPropertyName("weight")]
    public string Weight { get; set; } = string.Empty;
}

/// <summary>
///     新称重接口响应模型
/// </summary>
public class NewWeighingResponse
{
    /// <summary>
    ///     响应代码
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    ///     响应消息
    /// </summary>
    [JsonPropertyName("msg")]
    public string Msg { get; set; } = string.Empty;

    /// <summary>
    ///     错误消息
    /// </summary>
    [JsonPropertyName("errMsg")]
    public string? ErrMsg { get; set; }

    /// <summary>
    ///     时间戳
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    /// <summary>
    ///     响应数据
    /// </summary>
    [JsonPropertyName("data")]
    public NewWeighingResponseData? Data { get; set; }

    /// <summary>
    ///     是否成功（code为"0"表示成功）
    /// </summary>
    public bool IsSuccess
    {
        get => Code == "0";
    }
}

/// <summary>
///     新称重接口响应数据模型
/// </summary>
public class NewWeighingResponseData
{
    /// <summary>
    ///     承运商代码
    /// </summary>
    [JsonPropertyName("carrierCode")]
    public string CarrierCode { get; set; } = string.Empty;

    /// <summary>
    ///     省份名称
    /// </summary>
    [JsonPropertyName("provinceName")]
    public string ProvinceName { get; set; } = string.Empty;
}
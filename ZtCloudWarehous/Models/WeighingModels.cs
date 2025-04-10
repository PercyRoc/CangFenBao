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
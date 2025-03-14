using System.Text.Json.Serialization;

namespace Presentation_ZtCloudWarehous.Models;

/// <summary>
///     称重请求模型
/// </summary>
public class WeighingRequest
{
    // 公共参数
    [JsonPropertyName("api")] public string Api { get; set; } = "shanhaitong.wms.dws.weight";

    [JsonPropertyName("companyCode")] public string CompanyCode { get; set; } = string.Empty;

    [JsonPropertyName("appkey")] public string AppKey { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    [JsonPropertyName("sign_method")] public string SignMethod { get; set; } = "md5";

    [JsonPropertyName("sign")] public string Sign { get; set; } = string.Empty;

    [JsonPropertyName("v")] public string Version { get; set; } = "1.0.0";

    // 业务参数
    [JsonPropertyName("tenantId")] public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("warehouseCode")] public string WarehouseCode { get; set; } = string.Empty;

    [JsonPropertyName("waybillCode")] public string WaybillCode { get; set; } = string.Empty;

    [JsonPropertyName("packagingMaterialCode")]
    public string PackagingMaterialCode { get; set; } = string.Empty;

    [JsonPropertyName("actualWeight")] public decimal ActualWeight { get; set; }

    [JsonPropertyName("actualVolume")] public decimal? ActualVolume { get; set; }

    [JsonPropertyName("weighingEquipment")]
    public int WeighingEquipment { get; set; } = 1; // 默认为皮带秤称重设备

    [JsonPropertyName("equipmentCode")] public string EquipmentCode { get; set; } = string.Empty;

    [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("userRealName")] public string UserRealName { get; set; } = string.Empty;
}

/// <summary>
///     称重响应模型
/// </summary>
public class WeighingResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }

    [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;

    [JsonPropertyName("result")] public object? Result { get; set; }
}
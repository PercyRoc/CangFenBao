using System.Text.Json.Serialization;

namespace ZtCloudWarehous.Models;

/// <summary>
///     称重请求模型
/// </summary>
public class WeighingRequest
{
    // 公共参数
    [JsonPropertyName("api")] internal string Api { get; set; } = "shanhaitong.wms.dws.weight";

    [JsonPropertyName("companyCode")] internal string CompanyCode { get; set; } = string.Empty;

    [JsonPropertyName("appkey")] internal string AppKey { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    internal string Timestamp { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    [JsonPropertyName("sign_method")] internal string SignMethod { get; set; } = "md5";

    [JsonPropertyName("sign")] public string Sign { get; set; } = string.Empty;

    [JsonPropertyName("v")] internal string Version { get; set; } = "1.0.0";

    // 业务参数
    [JsonPropertyName("tenantId")] internal string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("warehouseCode")] internal string WarehouseCode { get; set; } = string.Empty;

    [JsonPropertyName("waybillCode")] internal string WaybillCode { get; set; } = string.Empty;

    [JsonPropertyName("packagingMaterialCode")]
    internal string PackagingMaterialCode { get; set; } = string.Empty;

    [JsonPropertyName("actualWeight")] internal decimal ActualWeight { get; set; }

    [JsonPropertyName("actualVolume")] internal decimal? ActualVolume { get; set; }

    [JsonPropertyName("weighingEquipment")]
    internal int WeighingEquipment { get; set; } = 1; // 默认为皮带秤称重设备

    [JsonPropertyName("equipmentCode")] public string EquipmentCode { get; set; } = string.Empty;

    [JsonPropertyName("userId")] internal string UserId { get; set; } = string.Empty;

    [JsonPropertyName("userRealName")] internal string UserRealName { get; set; } = string.Empty;
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
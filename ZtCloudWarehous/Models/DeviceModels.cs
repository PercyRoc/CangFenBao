using System.Text.Json.Serialization;

namespace ZtCloudWarehous.Models;

/// <summary>
///     设备注册请求
/// </summary>
public class DeviceRegisterRequest
{
    [JsonPropertyName("wareHouseCode")] public string WareHouseCode { get; set; } = string.Empty;

    [JsonPropertyName("equipmentCode")] public string EquipmentCode { get; set; } = string.Empty;

    [JsonPropertyName("equipmentType")] public string EquipmentType { get; set; } = string.Empty;

    [JsonPropertyName("manufacturer")] public string Manufacturer { get; set; } = string.Empty;
}

/// <summary>
///     设备基础请求（上线、下线、在线通知）
/// </summary>
public class DeviceBaseRequest
{
    [JsonPropertyName("wareHouseCode")] public string WareHouseCode { get; set; } = string.Empty;

    [JsonPropertyName("equipmentCode")] public string EquipmentCode { get; set; } = string.Empty;
}

/// <summary>
///     设备动作数据同步请求
/// </summary>
public class DeviceActionRequest
{
    [JsonPropertyName("wareHouseCode")] public string WareHouseCode { get; set; } = string.Empty;

    [JsonPropertyName("equipmentCode")] public string EquipmentCode { get; set; } = string.Empty;

    [JsonPropertyName("data")] public List<object> Data { get; set; } = new();

    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;

    [JsonPropertyName("operatTime")] public DateTime OperatTime { get; set; } = DateTime.Now;
}

/// <summary>
///     业务数据同步请求
/// </summary>
public class BusinessDataRequest
{
    [JsonPropertyName("wareHouseCode")] public string WareHouseCode { get; set; } = string.Empty;

    [JsonPropertyName("equipmentCode")] public string EquipmentCode { get; set; } = string.Empty;

    [JsonPropertyName("data")] public BusinessData Data { get; set; } = new();
}

/// <summary>
///     业务数据
/// </summary>
public class BusinessData
{
    [JsonPropertyName("total")] public int Total { get; set; }

    [JsonPropertyName("successQty")] public int SuccessQty { get; set; }

    [JsonPropertyName("failQty")] public int FailQty { get; set; }
}

/// <summary>
///     设备接口响应
/// </summary>
public class DeviceResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }

    [JsonPropertyName("msg")] public string Msg { get; set; } = string.Empty;

    [JsonPropertyName("data")] public object? Data { get; set; }

    [JsonPropertyName("equipmentCode")] public string EquipmentCode { get; set; } = string.Empty;
}
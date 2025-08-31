using Newtonsoft.Json;

namespace ShanghaiModuleBelt.Models.Sto;

/// <summary>
///     申通仓客户出库自动揽收接口请求模型
/// </summary>
public class StoAutoReceiveRequest
{
    [JsonProperty("whCode")] public required string WhCode { get; set; }

    [JsonProperty("orgCode")] public required string OrgCode { get; set; }

    [JsonProperty("userCode")] public required string UserCode { get; set; }

    [JsonProperty("packages")] public required List<Package> Packages { get; set; } = new();
}

/// <summary>
///     包裹信息
/// </summary>
public class Package
{
    [JsonProperty("waybillNo")] public required string WaybillNo { get; set; }

    [JsonProperty("weight")] public required string Weight { get; set; } // 重量，单位kg，精确2位小数

    [JsonProperty("opTime")] public required string OpTime { get; set; } // 揽收时间
}
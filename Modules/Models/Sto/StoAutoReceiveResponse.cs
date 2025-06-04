using System.Collections.Generic;
using Newtonsoft.Json;

namespace ShanghaiModuleBelt.Models.Sto;

/// <summary>
/// 申通仓客户出库自动揽收接口响应模型
/// </summary>
public class StoAutoReceiveResponse
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("errorCode")]
    public string ErrorCode { get; set; }

    [JsonProperty("errorMsg")]
    public string ErrorMsg { get; set; }

    // 根据申通文档，data节点可能不存在或为空，暂时注释掉或者根据实际情况调整
    // [JsonProperty("data")]
    // public StoResponseData Data { get; set; }
}

// public class StoResponseData
// {
//     [JsonProperty("respCode")]
//     public string RespCode { get; set; }
//
//     [JsonProperty("resMessage")]
//     public string ResMessage { get; set; }
//
//     [JsonProperty("data")]
//     public List<RecordError> Data { get; set; }
// }

// public class RecordError
// {
//     [JsonProperty("waybillNo")]
//     public string WaybillNo { get; set; }
//
//     [JsonProperty("errorDescription")]
//     public string ErrorDescription { get; set; }
// } 
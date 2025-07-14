using System.Text.Json.Serialization;

namespace DongtaiFlippingBoardMachine.Models;

/// <summary>
///     中通分拣请求基础模型
/// </summary>
public class ZtoSortingBaseRequest
{
    /// <summary>
    ///     消息类型
    /// </summary>
    [JsonPropertyName("msgType")]
    public string MsgType { get; set; } = string.Empty;

    /// <summary>
    ///     公司英文缩写
    /// </summary>
    [JsonPropertyName("company_id")]
    public string CompanyId { get; set; } = string.Empty;

    /// <summary>
    ///     消息内容
    /// </summary>
    [JsonPropertyName("data")]
    public string Data { get; set; } = string.Empty;

    /// <summary>
    ///     md5(data+key)
    /// </summary>
    [JsonPropertyName("data_digest")]
    public string DataDigest { get; set; } = string.Empty;
}

/// <summary>
///     中通分拣基础响应
/// </summary>
public class ZtoSortingBaseResponse
{
    /// <summary>
    ///     状态编码 成功：SUCCESS
    /// </summary>
    [JsonPropertyName("statusCode")]
    public string StatusCode { get; set; } = string.Empty;

    /// <summary>
    ///     状态信息 true || false
    /// </summary>
    [JsonPropertyName("status")]
    public bool Status { get; set; }

    /// <summary>
    ///     错误信息
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    ///     正确返回结果
    /// </summary>
    [JsonPropertyName("result")]
    public string Result { get; set; } = string.Empty;
}

/// <summary>
///     通用API响应模型
/// </summary>
/// <typeparam name="T">数据载荷类型</typeparam>
public class ZtoApiResponse<T> : ZtoSortingBaseResponse
{
    /// <summary>
    ///     业务数据
    /// </summary>
    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

/// <summary>
///     流水线开停状态请求
/// </summary>
public class PipelineStatusRequest
{
    /// <summary>
    ///     分拣线编码
    /// </summary>
    [JsonPropertyName("pipeline")]
    public string Pipeline { get; set; } = string.Empty;

    /// <summary>
    ///     设备SNcode
    /// </summary>
    [JsonPropertyName("sortSnCode")]
    public string SortSnCode { get; set; } = string.Empty;

    /// <summary>
    ///     状态切换时间
    /// </summary>
    [JsonPropertyName("switchTime")]
    public long SwitchTime { get; set; }

    /// <summary>
    ///     流水线状态
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>
    ///     分拣模式
    /// </summary>
    [JsonPropertyName("sortMode")]
    public string SortMode { get; set; } = "Sorting";
}

/// <summary>
///     分拣信息请求
/// </summary>
public class SortingInfoRequest
{
    /// <summary>
    ///     运单编号
    /// </summary>
    [JsonPropertyName("billCode")]
    public string BillCode { get; set; } = string.Empty;

    /// <summary>
    ///     分拣线编码
    /// </summary>
    [JsonPropertyName("pipeline")]
    public string Pipeline { get; set; } = string.Empty;

    /// <summary>
    ///     设备SN
    /// </summary>
    [JsonPropertyName("sortSnCode")]
    public string SortSnCode { get; set; } = string.Empty;

    /// <summary>
    ///     扫描次数
    /// </summary>
    [JsonPropertyName("turnNumber")]
    public int TurnNumber { get; set; }

    /// <summary>
    ///     扫描时间
    /// </summary>
    [JsonPropertyName("requestTime")]
    public long RequestTime { get; set; }

    /// <summary>
    ///     重量
    /// </summary>
    [JsonPropertyName("weight")]
    public float? Weight { get; set; }

    /// <summary>
    ///     小车编号
    /// </summary>
    [JsonPropertyName("trayCode")]
    public string TrayCode { get; set; } = string.Empty;

    /// <summary>
    ///     是否重传
    /// </summary>
    [JsonPropertyName("repeat")]
    public int Repeat { get; set; } = 0;

    /// <summary>
    ///     厂商扩展字段
    /// </summary>
    [JsonPropertyName("extendedField")]
    public string ExtendedField { get; set; } = string.Empty;
}

/// <summary>
///     分拣信息响应数据载荷
/// </summary>
public class SortingInfoResponseData
{
    /// <summary>
    ///     分拣口编号
    /// </summary>
    [JsonPropertyName("sortPortCode")]
    public List<string> SortPortCode { get; set; } =
    [
    ];

    /// <summary>
    ///     分拣来源，如0:正常读码；1:人工补码；2:供包机补码；3:异常件
    /// </summary>
    [JsonPropertyName("sortSource")]
    public string SortSource { get; set; } = string.Empty;

    /// <summary>
    ///     单号
    /// </summary>
    [JsonPropertyName("billCode")]
    public string BillCode { get; set; } = string.Empty;

    /// <summary>
    ///     小车编号
    /// </summary>
    [JsonPropertyName("trayCode")]
    public string TrayCode { get; set; } = string.Empty;

    /// <summary>
    ///     异常码
    /// </summary>
    [JsonPropertyName("errorCode")]
    public string ErrorCode { get; set; } = string.Empty;

    /// <summary>
    ///     错误信息
    /// </summary>
    [JsonPropertyName("errorMsg")]
    public string ErrorMsg { get; set; } = string.Empty;

    /// <summary>
    ///     服务器时间
    /// </summary>
    [JsonPropertyName("serverTime")]
    public long ServerTime { get; set; }

    /// <summary>
    ///     是否已完成
    /// </summary>
    [JsonPropertyName("comp")]
    public bool Comp { get; set; }

    /// <summary>
    ///     是否通过重量检查，0:未通过，1:通过
    /// </summary>
    [JsonPropertyName("isCheckWeightPass")]
    public int IsCheckWeightPass { get; set; }

    /// <summary>
    ///     是否已完成
    /// </summary>
    [JsonPropertyName("isComp")]
    public bool IsComp { get; set; }

    /// <summary>
    ///     是否标准，0:否，1:是
    /// </summary>
    [JsonPropertyName("isStandard")]
    public int IsStandard { get; set; }

    /// <summary>
    ///     是否停止DWS，0:否，1:是
    /// </summary>
    [JsonPropertyName("isStopDws")]
    public int IsStopDws { get; set; }

    /// <summary>
    ///     分拣线编码
    /// </summary>
    [JsonPropertyName("pipeline")]
    public string Pipeline { get; set; } = string.Empty;

    /// <summary>
    ///     秤台设备序列号
    /// </summary>
    [JsonPropertyName("scaleSn")]
    public string ScaleSn { get; set; } = string.Empty;

    /// <summary>
    ///     停止DWS的信息
    /// </summary>
    [JsonPropertyName("stopDwsMsg")]
    public string StopDwsMsg { get; set; } = string.Empty;
}

/// <summary>
///     分拣结果推送请求
/// </summary>
public class SortingResultRequest
{
    /// <summary>
    ///     小车编码
    /// </summary>
    [JsonPropertyName("trayCode")]
    public string TrayCode { get; set; } = string.Empty;

    /// <summary>
    ///     运单编号
    /// </summary>
    [JsonPropertyName("billCode")]
    public string BillCode { get; set; } = string.Empty;

    /// <summary>
    ///     分拣线编码
    /// </summary>
    [JsonPropertyName("pipeline")]
    public string Pipeline { get; set; } = string.Empty;

    /// <summary>
    ///     设备SN
    /// </summary>
    [JsonPropertyName("sortSnCode")]
    public string SortSnCode { get; set; } = string.Empty;

    /// <summary>
    ///     分拣时间
    /// </summary>
    [JsonPropertyName("sortTime")]
    public long SortTime { get; set; }

    /// <summary>
    ///     扫描次数
    /// </summary>
    [JsonPropertyName("turnNumber")]
    public int TurnNumber { get; set; }

    /// <summary>
    ///     分拣口编号
    /// </summary>
    [JsonPropertyName("sortPortCode")]
    public string SortPortCode { get; set; } = string.Empty;

    /// <summary>
    ///     分拣来源
    /// </summary>
    [JsonPropertyName("sortSource")]
    public string SortSource { get; set; } = "0";

    /// <summary>
    ///     补码编码/异常码
    /// </summary>
    [JsonPropertyName("sortCode")]
    public string SortCode { get; set; } = string.Empty;

    /// <summary>
    ///     是否重传
    /// </summary>
    [JsonPropertyName("repeat")]
    public int Repeat { get; set; } = 0;
}

/// <summary>
///     分拣结果响应
/// </summary>
public class SortingResultResponse
{
    /// <summary>
    ///     单号
    /// </summary>
    [JsonPropertyName("billCode")]
    public string BillCode { get; set; } = string.Empty;

    /// <summary>
    ///     分拣线编码
    /// </summary>
    [JsonPropertyName("lineCode")]
    public string LineCode { get; set; } = string.Empty;

    /// <summary>
    ///     小车号
    /// </summary>
    [JsonPropertyName("trayCode")]
    public string TrayCode { get; set; } = string.Empty;

    /// <summary>
    ///     分拣口编码
    /// </summary>
    [JsonPropertyName("interfCode")]
    public string InterfCode { get; set; } = string.Empty;

    /// <summary>
    ///     补码编码
    /// </summary>
    [JsonPropertyName("sortCode")]
    public string SortCode { get; set; } = string.Empty;
}

/// <summary>
///     面单规则响应
/// </summary>
public class BillRuleResponse : ZtoSortingBaseResponse
{
    /// <summary>
    ///     面单规则数据
    /// </summary>
    [JsonPropertyName("data")]
    public BillRuleData? Data { get; set; }
}

/// <summary>
///     面单规则数据
/// </summary>
public class BillRuleData
{
    /// <summary>
    ///     单号正则
    /// </summary>
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;
}

/// <summary>
///     时间校验响应
/// </summary>
public class TimeInspectionResponse : ZtoSortingBaseResponse
{
    /// <summary>
    ///     时间数据
    /// </summary>
    [JsonPropertyName("data")]
    public TimeInspectionData? Data { get; set; }
}

/// <summary>
///     时间校验数据
/// </summary>
public class TimeInspectionData
{
    /// <summary>
    ///     上海时间
    /// </summary>
    [JsonPropertyName("time")]
    public long Time { get; set; }
}

/// <summary>
///     分拣信息专用响应模型
/// </summary>
public class SortingInfoResponse
{
    /// <summary>
    ///     状态编码 成功：SUCCESS
    /// </summary>
    [JsonPropertyName("statusCode")]
    public string StatusCode { get; set; } = string.Empty;

    /// <summary>
    ///     状态信息 true || false
    /// </summary>
    [JsonPropertyName("status")]
    public bool Status { get; set; }

    /// <summary>
    ///     错误信息
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    ///     分拣信息数据（对象类型，不是字符串）
    /// </summary>
    [JsonPropertyName("result")]
    public SortingInfoResponseData? Result { get; set; }
}
using Newtonsoft.Json;

namespace ShanghaiModuleBelt.Models.Yunda;

/// <summary>
///     韵达上传重量接口请求
/// </summary>
public class YundaUploadWeightRequest
{
    /// <summary>
    ///     合作商 id（注：由韵达达系统分配并配置绑定）
    /// </summary>
    [JsonProperty("partnerid")]
    public required string PartnerId { get; set; }

    /// <summary>
    ///     合作商密码（注：由韵达系统分配并配置绑定）
    /// </summary>
    [JsonProperty("password")]
    public required string Password { get; set; }

    /// <summary>
    ///     密钥（注：由韵达系统分配并配置绑定）
    /// </summary>
    [JsonProperty("rc4Key")]
    public required string Rc4Key { get; set; }

    /// <summary>
    ///     订单详情
    /// </summary>
    [JsonProperty("orders")]
    public required YundaOrders Orders { get; set; }
}

/// <summary>
///     韵达订单详情
/// </summary>
public class YundaOrders
{
    /// <summary>
    ///     称重机器序列号（注：由合作商提供给韵达系统进行配置绑定）
    /// </summary>
    [JsonProperty("gun_id")]
    public required long GunId { get; set; }

    /// <summary>
    ///     请求时间（yyyy-MM-dd HH:mm:ss ）
    /// </summary>
    [JsonProperty("request_time")]
    public required string RequestTime { get; set; }

    /// <summary>
    ///     订单列表
    /// </summary>
    [JsonProperty("orders")]
    public required List<YundaOrder> OrderList { get; set; } = [];
}

/// <summary>
///     韵达订单信息
/// </summary>
public class YundaOrder
{
    /// <summary>
    ///     数据唯一标志，用于标志返回结果
    /// </summary>
    [JsonProperty("id")]
    public required long Id { get; set; }

    /// <summary>
    ///     面单号（13位或者15位数字）
    /// </summary>
    [JsonProperty("doc_id")]
    public required long DocId { get; set; }

    /// <summary>
    ///     扫描站点（6位数字，必须为达对应站点）
    /// </summary>
    [JsonProperty("scan_site")]
    public required int ScanSite { get; set; }

    /// <summary>
    ///     扫描时间（格式为：yyyy-MM-dd HH:mm:ss ）
    /// </summary>
    [JsonProperty("scan_time")]
    public required string ScanTime { get; set; }

    /// <summary>
    ///     扫描员编码（必须为韵达内部的编码）
    /// </summary>
    [JsonProperty("scan_man")]
    public required string ScanMan { get; set; }

    /// <summary>
    ///     物品盖量（小效点后保留2位的浮点数，单位默认KG）
    /// </summary>
    [JsonProperty("obj_wei")]
    public required decimal ObjWei { get; set; }
}
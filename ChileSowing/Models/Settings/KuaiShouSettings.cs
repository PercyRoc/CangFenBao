using Common.Services.Settings;

namespace ChileSowing.Models.Settings;

/// <summary>
/// 快手接口配置
/// </summary>
[Configuration("KuaiShouSettings")]
public class KuaiShouSettings
{
    /// <summary>
    /// 是否启用快手接口
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 快手接口地址 (UAT环境)
    /// </summary>
    public string ApiUrl { get; set; } = "http://10.20.160.36:7001/ydadl/public/common/new/commitScanMsg.do";

    /// <summary>
    /// 快手设备号 - 扫描设备的唯一标识
    /// </summary>
    public string DeviceNum { get; set; } = "ChileSowing001";

    /// <summary>
    /// 扫描人员 - 操作人员ID或姓名
    /// </summary>
    public string ScanPerson { get; set; } = "System";

    /// <summary>
    /// 扫描类型
    /// </summary>
    public int ScanType { get; set; } = 1;

    /// <summary>
    /// 供件台 - 包裹进入分拣机的入口ID
    /// </summary>
    public int InductionId { get; set; } = 1;

    /// <summary>
    /// 生产线编码
    /// </summary>
    public int ProdLine { get; set; } = 1;

    /// <summary>
    /// 交叉带设备ID
    /// </summary>
    public int EquipmentId { get; set; } = 1;

    /// <summary>
    /// 场地编码
    /// </summary>
    public int PlaceCode { get; set; } = 1;

    /// <summary>
    /// 片区编码
    /// </summary>
    public string AreaCode { get; set; } = "DEFAULT";

    /// <summary>
    /// 产品类型
    /// </summary>
    public string ExpProdType { get; set; } = "STANDARD";

    /// <summary>
    /// 货样编码
    /// </summary>
    public int ObjId { get; set; } = 1;

    /// <summary>
    /// 默认重量 (当无法获取实际重量时使用)
    /// </summary>
    public float DefaultWeight { get; set; } = 1.0f;

    /// <summary>
    /// 请求超时时间 (毫秒)
    /// </summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>
    /// 是否记录详细日志
    /// </summary>
    public bool EnableDetailedLogging { get; set; } = true;

    /// <summary>
    /// 重试次数
    /// </summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>
    /// 重试间隔 (毫秒)
    /// </summary>
    public int RetryDelayMs { get; set; } = 1000;
} 
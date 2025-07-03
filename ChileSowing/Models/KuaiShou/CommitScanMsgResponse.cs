namespace ChileSowing.Models.KuaiShou;

/// <summary>
/// 快手提交扫描信息响应模型
/// </summary>
public class CommitScanMsgResponse
{
    /// <summary>
    /// 响应状态 - 来自dta元素的st属性，"ok"表示成功
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 结果代码 - 来自dta元素的res属性，"0"表示成功
    /// </summary>
    public string ResultCode { get; set; } = string.Empty;

    /// <summary>
    /// 请求标识 - 第一个d元素的值
    /// </summary>
    public string Flag { get; set; } = string.Empty;

    /// <summary>
    /// 区划代码 - 系统分配的目的地区划代码
    /// </summary>
    public string AddrCode { get; set; } = string.Empty;

    /// <summary>
    /// 逻辑格口 - 分配的逻辑格口号
    /// </summary>
    public string Lchute { get; set; } = string.Empty;

    /// <summary>
    /// 物理格口 - 分配的物理格口号
    /// </summary>
    public string Chute { get; set; } = string.Empty;

    /// <summary>
    /// 排名 (V1.0.4+) - 操作员排名
    /// </summary>
    public string JobRkg { get; set; } = string.Empty;

    /// <summary>
    /// 作业用时 (V1.0.4+)
    /// </summary>
    public string JobDur { get; set; } = string.Empty;

    /// <summary>
    /// 作业总量 (V1.0.4+)
    /// </summary>
    public string JobTotCnt { get; set; } = string.Empty;

    /// <summary>
    /// 平均效率 (V1.0.4+)
    /// </summary>
    public string JobAvgRt { get; set; } = string.Empty;

    /// <summary>
    /// 作业峰值 (V1.0.4+)
    /// </summary>
    public string JobVal { get; set; } = string.Empty;

    /// <summary>
    /// 备注 (V1.0.5+) - 附加通知信息
    /// </summary>
    public string Notice { get; set; } = string.Empty;

    /// <summary>
    /// 错误信息 - 用于存储错误描述
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// 是否请求成功
    /// </summary>
    public bool IsSuccess => Status == "ok" && ResultCode == "0";
} 
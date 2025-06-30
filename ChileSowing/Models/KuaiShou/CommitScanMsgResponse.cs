using System.Xml.Serialization;

namespace ChileSowing.Models.KuaiShou;

/// <summary>
/// 快手提交扫描信息响应模型
/// </summary>
[XmlRoot("response")]
public class CommitScanMsgResponse
{
    /// <summary>
    /// 请求标识 - 0: 正常, 1: 异常
    /// </summary>
    [XmlElement("flag")]
    public int Flag { get; set; }

    /// <summary>
    /// 区划代码 - 系统分配的目的地区划代码
    /// </summary>
    [XmlElement("addrCode")]
    public int AddrCode { get; set; }

    /// <summary>
    /// 逻辑格口 - 分配的逻辑格口号
    /// </summary>
    [XmlElement("lchute")]
    public int LChute { get; set; }

    /// <summary>
    /// 物理格口 - 分配的物理格口号
    /// </summary>
    [XmlElement("chute")]
    public string Chute { get; set; } = string.Empty;

    /// <summary>
    /// 排名 (V1.0.4+) - 操作员排名，为空返回0
    /// </summary>
    [XmlElement("jobRkg")]
    public int JobRkg { get; set; }

    /// <summary>
    /// 作业用时 (V1.0.4+) - 为空返回0
    /// </summary>
    [XmlElement("jobDur")]
    public int JobDur { get; set; }

    /// <summary>
    /// 作业总量 (V1.0.4+) - 为空返回0
    /// </summary>
    [XmlElement("jobTotCnt")]
    public int JobTotCnt { get; set; }

    /// <summary>
    /// 平均效率 (V1.0.4+) - 为空返回0
    /// </summary>
    [XmlElement("jobAvgRt")]
    public int JobAvgRt { get; set; }

    /// <summary>
    /// 作业峰值 (V1.0.4+) - 为空返回0
    /// </summary>
    [XmlElement("jobVal")]
    public int JobVal { get; set; }

    /// <summary>
    /// 备注 (V1.0.5+) - 附加通知信息，为空返回 ""
    /// </summary>
    [XmlElement("notice")]
    public string Notice { get; set; } = string.Empty;

    /// <summary>
    /// 是否请求成功
    /// </summary>
    [XmlIgnore]
    public bool IsSuccess => Flag == 0;

    /// <summary>
    /// 错误描述（当Flag != 0时）
    /// </summary>
    [XmlIgnore]
    public string ErrorMessage => Flag != 0 ? $"Request failed with flag: {Flag}" : string.Empty;
} 
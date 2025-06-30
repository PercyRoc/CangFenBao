using System.Xml.Serialization;

namespace ChileSowing.Models.KuaiShou;

/// <summary>
/// 快手提交扫描信息请求模型
/// </summary>
[XmlRoot("request")]
public class CommitScanMsgRequest
{
    /// <summary>
    /// 单号 - 包裹运单号
    /// </summary>
    [XmlElement("shipId")]
    public string ShipId { get; set; } = string.Empty;

    /// <summary>
    /// 重量 - 包裹重量
    /// </summary>
    [XmlElement("weight")]
    public float Weight { get; set; }

    /// <summary>
    /// 供件台 - 包裹进入分拣机的入口ID
    /// </summary>
    [XmlElement("inductionId")]
    public int InductionId { get; set; }

    /// <summary>
    /// 快手设备号 - 扫描设备的唯一标识
    /// </summary>
    [XmlElement("deviceNum")]
    public string DeviceNum { get; set; } = string.Empty;

    /// <summary>
    /// 扫描人员 - 操作人员ID或姓名
    /// </summary>
    [XmlElement("scanPerson")]
    public string ScanPerson { get; set; } = string.Empty;

    /// <summary>
    /// 扫描类型
    /// </summary>
    [XmlElement("scanType")]
    public int ScanType { get; set; }

    /// <summary>
    /// 备注字段 - 附加备注信息
    /// </summary>
    [XmlElement("remarkField")]
    public string RemarkField { get; set; } = string.Empty;

    /// <summary>
    /// 快件长度 (V1.0.5+)
    /// </summary>
    [XmlElement("length")]
    public string Length { get; set; } = string.Empty;

    /// <summary>
    /// 快件宽度 (V1.0.5+)
    /// </summary>
    [XmlElement("width")]
    public string Width { get; set; } = string.Empty;

    /// <summary>
    /// 快件高度 (V1.0.5+)
    /// </summary>
    [XmlElement("height")]
    public string Height { get; set; } = string.Empty;

    /// <summary>
    /// 快件体积 (V1.0.5+)
    /// </summary>
    [XmlElement("volume")]
    public string Volume { get; set; } = string.Empty;

    /// <summary>
    /// 生产线编码 (V1.0.6+)
    /// </summary>
    [XmlElement("prodLine")]
    public int ProdLine { get; set; }

    /// <summary>
    /// 货样编码 (V1.0.7+)
    /// </summary>
    [XmlElement("ObjId")]
    public int ObjId { get; set; }

    /// <summary>
    /// 产品类型 (V1.0.8+)
    /// </summary>
    [XmlElement("ExpProdType")]
    public string ExpProdType { get; set; } = string.Empty;

    /// <summary>
    /// 片区编码 (V1.0.9+)
    /// </summary>
    [XmlElement("areaCode")]
    public string AreaCode { get; set; } = string.Empty;

    /// <summary>
    /// 交叉带设备ID (V1.1.0+)
    /// </summary>
    [XmlElement("equipmentId")]
    public int EquipmentId { get; set; }

    /// <summary>
    /// 场地编码 (V1.1.0+)
    /// </summary>
    [XmlElement("placeCode")]
    public int PlaceCode { get; set; }

    /// <summary>
    /// 扫描时间 (V1.1.1+) 格式: yyyy-MM-dd HH:mm:ss
    /// </summary>
    [XmlElement("rcvTime")]
    public string RcvTime { get; set; } = string.Empty;
} 
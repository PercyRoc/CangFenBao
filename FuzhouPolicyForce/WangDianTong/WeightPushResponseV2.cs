namespace FuzhouPolicyForce.WangDianTong;

/// <summary>
///     旺店通重量回传响应结果V2 (符合新文档)
/// </summary>
public class WeightPushResponseV2
{
    /// <summary>
    ///     错误码，"0"表示成功，其他表示失败 (根据现有Response结构推测包含)
    /// </summary>
    public string? Code { get; set; }

    /// <summary>
    ///     错误描述 (根据现有Response结构推测包含)
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    ///     出口通道号。
    /// </summary>
    public string? ExportNum { get; set; }

    /// <summary>
    ///     收件人省，比如：北京
    /// </summary>
    public string? ReceiverProvince { get; set; }

    /// <summary>
    ///     收件人市，比如：北京市
    /// </summary>
    public string? ReceiverCity { get; set; }

    /// <summary>
    ///     收件人区，比如：朝阳区
    /// </summary>
    public string? ReceiverDistrict { get; set; }

    /// <summary>
    ///     是否成功 (辅助属性，根据Code判断)
    /// </summary>
    public bool IsSuccess
    {
        get => Code == "0";
    }
}
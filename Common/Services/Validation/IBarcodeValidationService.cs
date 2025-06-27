using System.ComponentModel;

namespace Common.Services.Validation;

/// <summary>
/// 单号校验服务接口
/// </summary>
public interface IBarcodeValidationService
{
    /// <summary>
    /// 校验单号是否有效
    /// </summary>
    /// <param name="barcode">要校验的单号</param>
    /// <returns>校验结果</returns>
    BarcodeValidationResult ValidateBarcode(string barcode);

    /// <summary>
    /// 获取单号类型
    /// </summary>
    /// <param name="barcode">要检测的单号</param>
    /// <returns>单号类型，如果无法识别则返回Unknown</returns>
    BarcodeType GetBarcodeType(string barcode);
}

/// <summary>
/// 单号校验结果
/// </summary>
public class BarcodeValidationResult
{
    /// <summary>
    /// 是否校验通过
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// 单号类型
    /// </summary>
    public BarcodeType BarcodeType { get; set; }

    /// <summary>
    /// 错误消息（校验失败时）
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 创建成功的校验结果
    /// </summary>
    /// <param name="barcodeType">单号类型</param>
    /// <returns>校验结果</returns>
    public static BarcodeValidationResult Success(BarcodeType barcodeType)
    {
        return new BarcodeValidationResult
        {
            IsValid = true,
            BarcodeType = barcodeType
        };
    }

    /// <summary>
    /// 创建失败的校验结果
    /// </summary>
    /// <param name="errorMessage">错误消息</param>
    /// <param name="barcodeType">单号类型（可选）</param>
    /// <returns>校验结果</returns>
    public static BarcodeValidationResult Failure(string errorMessage, BarcodeType barcodeType = BarcodeType.Unknown)
    {
        return new BarcodeValidationResult
        {
            IsValid = false,
            BarcodeType = barcodeType,
            ErrorMessage = errorMessage
        };
    }
}

/// <summary>
/// 单号类型枚举
/// </summary>
public enum BarcodeType
{
    /// <summary>
    /// 未知类型
    /// </summary>
    [Description("未知类型")]
    Unknown = 0,

    /// <summary>
    /// 内部包裹流转码（9开头，10位数字）
    /// </summary>
    [Description("内部包裹流转码")]
    InternalPackage = 1,

    /// <summary>
    /// 标准快递面单（8开头，13位数字）
    /// </summary>
    [Description("标准快递面单")]
    StandardExpress = 2,

    /// <summary>
    /// API直连电子面单（6开头，13位数字）
    /// </summary>
    [Description("API直连电子面单")]
    ApiDirectElectronic = 3,

    /// <summary>
    /// 吉时达电子面单（2开头13位或JS0开头15位）
    /// </summary>
    [Description("吉时达电子面单")]
    JishidaElectronic = 4,

    /// <summary>
    /// 淘宝/菜鸟电子面单（67开头，13位数字，EAN-13校验）
    /// </summary>
    [Description("淘宝/菜鸟电子面单")]
    TaobaoElectronic = 5
} 
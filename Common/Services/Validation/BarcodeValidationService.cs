using System.Text.RegularExpressions;
using Serilog;

namespace Common.Services.Validation;

/// <summary>
///     单号校验服务实现
/// </summary>
public class BarcodeValidationService : IBarcodeValidationService
{
    // 正则表达式模式
    private static readonly Regex InternalPackagePattern = new(@"^9\d{9}$", RegexOptions.Compiled);
    private static readonly Regex StandardExpressPattern = new(@"^8\d{12}$", RegexOptions.Compiled);
    private static readonly Regex ApiDirectElectronicPattern = new(@"^6\d{12}$", RegexOptions.Compiled);
    private static readonly Regex JishidaElectronicPattern = new(@"^(2\d{12}|JS0\d{12})$", RegexOptions.Compiled);
    private static readonly Regex TaobaoElectronicPattern = new(@"^67\d{11}$", RegexOptions.Compiled);

    /// <summary>
    ///     校验单号是否有效
    /// </summary>
    /// <param name="barcode">要校验的单号</param>
    /// <returns>校验结果</returns>
    public BarcodeValidationResult ValidateBarcode(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return BarcodeValidationResult.Failure("单号不能为空");
        }

        // 去除首尾空格
        barcode = barcode.Trim();

        try
        {
            // 按照优先级顺序检查各种类型
            var barcodeType = GetBarcodeType(barcode);

            var result = barcodeType switch
            {
                BarcodeType.InternalPackage => ValidateInternalPackage(barcode),
                BarcodeType.StandardExpress => ValidateStandardExpress(barcode),
                BarcodeType.ApiDirectElectronic => ValidateApiDirectElectronic(barcode),
                BarcodeType.JishidaElectronic => ValidateJishidaElectronic(barcode),
                BarcodeType.TaobaoElectronic => ValidateTaobaoElectronic(barcode),
                _ => BarcodeValidationResult.Failure("无效的单号格式")
            };

            // 记录校验日志
            if (result.IsValid)
            {
                Log.Information("单号校验通过: {Barcode}, 类型: {Type}", barcode, result.BarcodeType);
            }
            else
            {
                Log.Warning("单号校验失败: {Barcode}, 错误: {Error}", barcode, result.ErrorMessage);
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "校验单号时发生异常: {Barcode}", barcode);
            return BarcodeValidationResult.Failure($"校验过程中发生错误: {ex.Message}");
        }
    }

    /// <summary>
    ///     获取单号类型
    /// </summary>
    /// <param name="barcode">要检测的单号</param>
    /// <returns>单号类型，如果无法识别则返回Unknown</returns>
    public BarcodeType GetBarcodeType(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return BarcodeType.Unknown;
        }

        barcode = barcode.Trim();

        // 按照优先级顺序检查
        if (InternalPackagePattern.IsMatch(barcode))
            return BarcodeType.InternalPackage;

        if (StandardExpressPattern.IsMatch(barcode))
            return BarcodeType.StandardExpress;

        if (ApiDirectElectronicPattern.IsMatch(barcode))
            return BarcodeType.ApiDirectElectronic;

        if (JishidaElectronicPattern.IsMatch(barcode))
            return BarcodeType.JishidaElectronic;

        if (TaobaoElectronicPattern.IsMatch(barcode))
            return BarcodeType.TaobaoElectronic;

        return BarcodeType.Unknown;
    }

    /// <summary>
    ///     校验内部包裹流转码
    /// </summary>
    private static BarcodeValidationResult ValidateInternalPackage(string barcode)
    {
        if (InternalPackagePattern.IsMatch(barcode))
        {
            return BarcodeValidationResult.Success(BarcodeType.InternalPackage);
        }
        return BarcodeValidationResult.Failure("内部包裹流转码格式错误，应为9开头的10位数字", BarcodeType.InternalPackage);
    }

    /// <summary>
    ///     校验标准快递面单
    /// </summary>
    private static BarcodeValidationResult ValidateStandardExpress(string barcode)
    {
        if (StandardExpressPattern.IsMatch(barcode))
        {
            return BarcodeValidationResult.Success(BarcodeType.StandardExpress);
        }
        return BarcodeValidationResult.Failure("标准快递面单格式错误，应为8开头的13位数字", BarcodeType.StandardExpress);
    }

    /// <summary>
    ///     校验API直连电子面单
    /// </summary>
    private static BarcodeValidationResult ValidateApiDirectElectronic(string barcode)
    {
        if (ApiDirectElectronicPattern.IsMatch(barcode))
        {
            return BarcodeValidationResult.Success(BarcodeType.ApiDirectElectronic);
        }
        return BarcodeValidationResult.Failure("API直连电子面单格式错误，应为6开头的13位数字", BarcodeType.ApiDirectElectronic);
    }

    /// <summary>
    ///     校验吉时达电子面单
    /// </summary>
    private static BarcodeValidationResult ValidateJishidaElectronic(string barcode)
    {
        if (!JishidaElectronicPattern.IsMatch(barcode))
        {
            return BarcodeValidationResult.Failure("吉时达电子面单格式错误，应为2开头的13位数字或JS0开头的15位字符", BarcodeType.JishidaElectronic);
        }

        // 对于JS开头的15位单号，需要进行校验位校验
        if (barcode.StartsWith("JS0") && barcode.Length == 15)
        {
            try
            {
                if (ValidateJishidaChecksum(barcode))
                {
                    return BarcodeValidationResult.Success(BarcodeType.JishidaElectronic);
                }
                return BarcodeValidationResult.Failure("吉时达电子面单校验位验证失败", BarcodeType.JishidaElectronic);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "吉时达电子面单校验位计算异常: {Barcode}", barcode);
                return BarcodeValidationResult.Failure($"吉时达电子面单校验位计算错误: {ex.Message}", BarcodeType.JishidaElectronic);
            }
        }

        // 对于2开头的13位单号，只需要格式校验
        return BarcodeValidationResult.Success(BarcodeType.JishidaElectronic);
    }

    /// <summary>
    ///     校验淘宝/菜鸟电子面单
    /// </summary>
    private static BarcodeValidationResult ValidateTaobaoElectronic(string barcode)
    {
        if (!TaobaoElectronicPattern.IsMatch(barcode))
        {
            return BarcodeValidationResult.Failure("淘宝/菜鸟电子面单格式错误，应为67开头的13位数字", BarcodeType.TaobaoElectronic);
        }

        try
        {
            if (ValidateEan13Checksum(barcode))
            {
                return BarcodeValidationResult.Success(BarcodeType.TaobaoElectronic);
            }
            return BarcodeValidationResult.Failure("淘宝/菜鸟电子面单EAN-13校验位验证失败", BarcodeType.TaobaoElectronic);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "淘宝/菜鸟电子面单校验位计算异常: {Barcode}", barcode);
            return BarcodeValidationResult.Failure($"淘宝/菜鸟电子面单校验位计算错误: {ex.Message}", BarcodeType.TaobaoElectronic);
        }
    }

    /// <summary>
    ///     校验吉时达电子面单的校验位（JS开头的15位单号）
    /// </summary>
    /// <param name="barcode">15位的JS开头单号</param>
    /// <returns>校验是否通过</returns>
    private static bool ValidateJishidaChecksum(string barcode)
    {
        if (barcode.Length != 15 || !barcode.StartsWith("JS0"))
        {
            throw new ArgumentException("输入必须是JS0开头的15位字符串");
        }

        // 提取单号的第6到第13位字符作为数字部分（总共8位数字）
        // 根据Java代码 substring(5, 13)，从索引5开始取8个字符
        var numberString = barcode.Substring(5, 8);

        // 验证是否为8位数字
        if (!Regex.IsMatch(numberString, @"^\d{8}$"))
        {
            throw new ArgumentException("提取的数字部分必须是8位数字");
        }

        // 提取单号的最后两位作为原始校验位
        var originalChecksum = barcode.Substring(13, 2);

        // 生成新的校验位
        var calculatedChecksum = GenerateJishidaChecksum(numberString);

        // 比较校验位
        return string.Equals(originalChecksum, calculatedChecksum, StringComparison.Ordinal);
    }

    /// <summary>
    ///     生成吉时达校验位
    /// </summary>
    /// <param name="input">8位数字字符串</param>
    /// <returns>两位校验码</returns>
    private static string GenerateJishidaChecksum(string input)
    {
        if (string.IsNullOrEmpty(input) || input.Length != 8 || !Regex.IsMatch(input, @"^\d{8}$"))
        {
            throw new ArgumentException("输入必须是8位的数字字符串");
        }

        var sum = 0;
        foreach (var c in input)
        {
            // 将每个字符的ASCII码加上'2'的ASCII码后累加
            sum += c + '2';
        }

        // 对累加和进行按位取反操作（~），结果视为无符号长整型
        var unsignedResult = ~sum & 0xFFFFFFFFL;

        // 对结果再进行按位或操作
        unsignedResult = unsignedResult | 85;

        // 对结果取100的模，得到一个0-99的数字
        var checksum = unsignedResult % 100;

        // 将结果格式化为两位字符串，不足则前面补0
        return checksum.ToString("D2");
    }

    /// <summary>
    ///     校验EAN-13校验位（淘宝/菜鸟电子面单）
    /// </summary>
    /// <param name="barcode">13位数字字符串</param>
    /// <returns>校验是否通过</returns>
    private static bool ValidateEan13Checksum(string barcode)
    {
        if (barcode.Length != 13 || !Regex.IsMatch(barcode, @"^\d{13}$"))
        {
            throw new ArgumentException("EAN-13 code must be 13 digits");
        }

        // 提取前12位作为基础码
        var ean12 = barcode.Substring(0, 12);

        // 提取第13位作为原始校验位
        var originalCheckDigit = int.Parse(barcode.Substring(12, 1));

        // 计算校验位
        var calculatedCheckDigit = CalculateEan13CheckDigit(ean12);

        // 比较校验位
        return originalCheckDigit == calculatedCheckDigit;
    }

    /// <summary>
    ///     计算EAN-13校验位
    /// </summary>
    /// <param name="ean12">12位数字字符串</param>
    /// <returns>校验位（0-9）</returns>
    private static int CalculateEan13CheckDigit(string ean12)
    {
        if (string.IsNullOrEmpty(ean12) || ean12.Length != 12 || !Regex.IsMatch(ean12, @"^\d{12}$"))
        {
            throw new ArgumentException("EAN-12 code must be 12 digits");
        }

        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            var digit = int.Parse(ean12[i].ToString());
            // 从左到右，第1、3、5...位（索引0、2、4...）乘以1，第2、4、6...位（索引1、3、5...）乘以3
            sum += i % 2 == 0 ? digit * 1 : digit * 3;
        }

        var remainder = sum % 10;
        return remainder == 0 ? 0 : 10 - remainder;
    }
}
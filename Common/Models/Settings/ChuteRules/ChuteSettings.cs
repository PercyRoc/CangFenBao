using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Common.Services.Settings;
using Serilog;

namespace Common.Models.Settings.ChuteRules;

[Configuration("ChuteSettings")]
public class ChuteSettings : BindableBase
{
    private int _chuteCount = 1;
    private Dictionary<int, BarcodeMatchRule> _chuteRules = [];
    private int _errorChuteNumber;
    private int _noReadChuteNumber;
    private int _refundChuteNumber;
    private int _timeoutChuteNumber;
    private int _weightMismatchChuteNumber;

    [Range(1, 100, ErrorMessage = "格口数量必须在1-100之间")]
    public int ChuteCount
    {
        get => _chuteCount;
        set => SetProperty(ref _chuteCount, value);
    }

    [Range(0, 100, ErrorMessage = "异常格口必须在0-100之间")]
    public int ErrorChuteNumber
    {
        get => _errorChuteNumber;
        set => SetProperty(ref _errorChuteNumber, value);
    }

    [Range(0, 100, ErrorMessage = "超时格口必须在0-100之间")]
    public int TimeoutChuteNumber
    {
        get => _timeoutChuteNumber;
        set => SetProperty(ref _timeoutChuteNumber, value);
    }

    [Range(0, 100, ErrorMessage = "NoRead格口必须在0-100之间")]
    public int NoReadChuteNumber
    {
        get => _noReadChuteNumber;
        set => SetProperty(ref _noReadChuteNumber, value);
    }

    [Range(0, 100, ErrorMessage = "重量不匹配格口必须在0-100之间")]
    public int WeightMismatchChuteNumber
    {
        get => _weightMismatchChuteNumber;
        set => SetProperty(ref _weightMismatchChuteNumber, value);
    }

    [Range(0, 100, ErrorMessage = "退款格口必须在0-100之间")]
    public int RefundChuteNumber
    {
        get => _refundChuteNumber;
        set => SetProperty(ref _refundChuteNumber, value);
    }

    public Dictionary<int, BarcodeMatchRule> ChuteRules
    {
        get => _chuteRules;
        set => SetProperty(ref _chuteRules, value);
    }

    /// <summary>
    ///     根据条码查找匹配的格口
    /// </summary>
    /// <param name="barcode">包裹条码</param>
    /// <returns>匹配的格口号，如果没有匹配则返回null</returns>
    public int? FindMatchingChute(string barcode)
    {
        return FindMatchingChute(barcode, null);
    }

    /// <summary>
    ///     根据条码和重量查找匹配的格口
    /// </summary>
    /// <param name="barcode">包裹条码</param>
    /// <param name="weight">包裹重量（克）</param>
    /// <returns>匹配的格口号，如果没有匹配则返回null</returns>
    public int? FindMatchingChute(string barcode, double? weight)
    {
        if (string.IsNullOrEmpty(barcode)) return null;

        // 按优先级排序：先处理有重量规则的非默认规则，再处理无重量规则的非默认规则，最后处理默认规则
        var orderedRules = ChuteRules
            .OrderBy(r => r.Value.IsEffectivelyDefault())
            .ThenByDescending(r => r.Value.HasWeightRule())
            .ToList();

        foreach (var (chuteNumber, chuteRule) in orderedRules)
        {
            if (!chuteRule.IsMatching(barcode, weight)) continue;
            
            // 如果规则是默认规则，并且不是唯一的匹配，则跳过继续寻找更具体的规则。
            // 如果只有默认规则匹配，或者该格口是唯一的匹配，则返回它。
            if (!chuteRule.IsEffectivelyDefault()) return chuteNumber; // 返回第一个匹配的非默认规则
            
            // 检查是否存在其他非默认规则也匹配
            var hasSpecificMatch = ChuteRules.Any(r => r.Key != chuteNumber && r.Value.IsMatching(barcode, weight) && !r.Value.IsEffectivelyDefault());
            if (hasSpecificMatch)
            {
                continue; // 存在更具体的匹配，跳过此默认规则
            }
            return chuteNumber; // 返回第一个匹配的默认规则
        }

        return null;
    }
}

public class BarcodeMatchRule : BindableBase
{
    private string _contains = string.Empty;
    private string _endsWith = string.Empty;
    private bool _isAlphanumeric;
    private bool _isDigitOnly;
    private bool _isLetterOnly;
    private int _maxLength;
    private double _maxWeight;
    private int _minLength;
    private double _minWeight;
    private string _notContains = string.Empty;
    private string _notEndsWith = string.Empty;
    private string _notStartsWith = string.Empty;
    private string _regexPattern = "(?=.*(?))";
    private string _startsWith = string.Empty;
    private bool _useWeightRule;

    public bool IsDigitOnly
    {
        get => _isDigitOnly;
        set => SetProperty(ref _isDigitOnly, value);
    }

    public bool IsLetterOnly
    {
        get => _isLetterOnly;
        set => SetProperty(ref _isLetterOnly, value);
    }

    public bool IsAlphanumeric
    {
        get => _isAlphanumeric;
        set => SetProperty(ref _isAlphanumeric, value);
    }

    [Range(0, int.MaxValue, ErrorMessage = "最小长度不能小于0")]
    public int MinLength
    {
        get => _minLength;
        set => SetProperty(ref _minLength, value);
    }

    [Range(0, int.MaxValue, ErrorMessage = "最大长度不能小于0")]
    public int MaxLength
    {
        get => _maxLength;
        set => SetProperty(ref _maxLength, value);
    }

    /// <summary>
    ///     是否启用重量规则
    /// </summary>
    public bool UseWeightRule
    {
        get => _useWeightRule;
        set => SetProperty(ref _useWeightRule, value);
    }

    /// <summary>
    ///     最小重量（克）
    /// </summary>
    [Range(0, double.MaxValue, ErrorMessage = "最小重量不能小于0")]
    public double MinWeight
    {
        get => _minWeight;
        set => SetProperty(ref _minWeight, value);
    }

    /// <summary>
    ///     最大重量（克）
    /// </summary>
    [Range(0, double.MaxValue, ErrorMessage = "最大重量不能小于0")]
    public double MaxWeight
    {
        get => _maxWeight;
        set => SetProperty(ref _maxWeight, value);
    }

    public string StartsWith
    {
        get => _startsWith;
        set => SetProperty(ref _startsWith, value);
    }

    public string EndsWith
    {
        get => _endsWith;
        set => SetProperty(ref _endsWith, value);
    }

    public string NotStartsWith
    {
        get => _notStartsWith;
        set => SetProperty(ref _notStartsWith, value);
    }

    public string NotEndsWith
    {
        get => _notEndsWith;
        set => SetProperty(ref _notEndsWith, value);
    }

    public string Contains
    {
        get => _contains;
        set => SetProperty(ref _contains, value);
    }

    public string NotContains
    {
        get => _notContains;
        set => SetProperty(ref _notContains, value);
    }

    public string RegexPattern
    {
        get => _regexPattern;
        set => SetProperty(ref _regexPattern, value);
    }

    /// <summary>
    ///     检查当前规则是否包含重量规则
    /// </summary>
    internal bool HasWeightRule()
    {
        return UseWeightRule && (MinWeight > 0 || MaxWeight > 0);
    }

    /// <summary>
    ///     检查当前规则是否是"默认"规则，即在没有其他特定条件的情况下会匹配任何条码。
    /// </summary>
    internal bool IsEffectivelyDefault()
    {
        // 如果所有条件都是其默认/空值，则认为该规则是"默认"规则
        return MinLength == 0 &&
               MaxLength == 0 &&
               !IsDigitOnly &&
               !IsLetterOnly &&
               !IsAlphanumeric &&
               !UseWeightRule &&
               string.IsNullOrEmpty(StartsWith) &&
               string.IsNullOrEmpty(EndsWith) &&
               string.IsNullOrEmpty(NotStartsWith) &&
               string.IsNullOrEmpty(NotEndsWith) &&
               string.IsNullOrEmpty(Contains) &&
               string.IsNullOrEmpty(NotContains) &&
               (string.IsNullOrEmpty(RegexPattern) || RegexPattern == "(?=.*(?))");
    }

    /// <summary>
    ///     检查当前规则是否为"仅字符类型"的规则（只设置了字符类型条件，其他都是默认值）
    /// </summary>
    private bool IsCharacterTypeOnlyRule()
    {
        return (IsDigitOnly || IsLetterOnly || IsAlphanumeric) &&
               MinLength == 0 &&
               MaxLength == 0 &&
               !UseWeightRule &&
               string.IsNullOrEmpty(StartsWith) &&
               string.IsNullOrEmpty(EndsWith) &&
               string.IsNullOrEmpty(NotStartsWith) &&
               string.IsNullOrEmpty(NotEndsWith) &&
               string.IsNullOrEmpty(Contains) &&
               string.IsNullOrEmpty(NotContains) &&
               (string.IsNullOrEmpty(RegexPattern) || RegexPattern == "(?=.*(?))");
    }

    /// <summary>
    ///     检查条码是否匹配当前规则
    /// </summary>
    /// <param name="barcode">包裹条码</param>
    /// <returns>是否匹配</returns>
    internal bool IsMatching(string barcode)
    {
        return IsMatching(barcode, null);
    }

    /// <summary>
    ///     检查条码和重量是否匹配当前规则
    /// </summary>
    /// <param name="barcode">包裹条码</param>
    /// <param name="weight">包裹重量（克）</param>
    /// <returns>是否匹配</returns>
    internal bool IsMatching(string barcode, double? weight)
    {
        // 检查是否为"仅字符类型"的默认规则，如果是则不匹配
        if (IsCharacterTypeOnlyRule())
        {
            return false; // 不匹配仅字符类型的默认规则
        }

        // 检查重量限制
        if (UseWeightRule && weight.HasValue)
        {
            var weightInGrams = weight.Value * 1000; // 转换为克
            if (MinWeight > 0 && weightInGrams < MinWeight) return false;
            if (MaxWeight > 0 && weightInGrams > MaxWeight) return false;
        }
        else if (UseWeightRule && !weight.HasValue)
        {
            // 如果规则要求重量但没有提供重量信息，则不匹配
            return false;
        }

        // 检查长度限制
        if (MinLength > 0 && barcode.Length < MinLength) return false;
        if (MaxLength > 0 && barcode.Length > MaxLength) return false;

        // 检查字符类型限制
        if (IsDigitOnly && !barcode.All(char.IsDigit)) return false;
        if (IsLetterOnly && !barcode.All(char.IsLetter)) return false;
        if (IsAlphanumeric && !barcode.All(static c => char.IsLetterOrDigit(c))) return false;

        // 检查前缀和后缀
        if (!string.IsNullOrEmpty(StartsWith))
        {
            var startValues = StartsWith.Replace("，", ",")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(static s => s.Trim())
                .Where(static s => !string.IsNullOrEmpty(s));
            if (!startValues.Any(start => barcode.StartsWith(start, StringComparison.Ordinal))) return false;
        }

        if (!string.IsNullOrEmpty(EndsWith))
        {
            var endValues = EndsWith.Replace("，", ",")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(static s => s.Trim())
                .Where(static s => !string.IsNullOrEmpty(s));
            if (!endValues.Any(end => barcode.EndsWith(end, StringComparison.Ordinal))) return false;
        }

        // 检查不包含的前缀和后缀
        if (!string.IsNullOrEmpty(NotStartsWith))
        {
            var notStartValues = NotStartsWith.Replace("，", ",")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(static s => s.Trim())
                .Where(static s => !string.IsNullOrEmpty(s));
            if (notStartValues.Any(start => barcode.StartsWith(start, StringComparison.Ordinal))) return false;
        }

        if (!string.IsNullOrEmpty(NotEndsWith))
        {
            var notEndValues = NotEndsWith.Replace("，", ",")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(static s => s.Trim())
                .Where(static s => !string.IsNullOrEmpty(s));
            if (notEndValues.Any(end => barcode.EndsWith(end, StringComparison.Ordinal))) return false;
        }

        // 检查包含和不包含的字符串
        if (!string.IsNullOrEmpty(Contains))
        {
            var containValues = Contains.Replace("，", ",")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(static s => s.Trim())
                .Where(static s => !string.IsNullOrEmpty(s));
            if (!containValues.Any(barcode.Contains)) return false;
        }

        if (!string.IsNullOrEmpty(NotContains))
        {
            var notContainValues = NotContains.Replace("，", ",")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(static s => s.Trim())
                .Where(static s => !string.IsNullOrEmpty(s));
            if (notContainValues.Any(barcode.Contains)) return false;
        }

        // 检查正则表达式
        if (string.IsNullOrEmpty(RegexPattern) || RegexPattern == "(?=.*(?))") return true;

        try
        {
            if (!Regex.IsMatch(barcode, RegexPattern))
                return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "正则表达式匹配错误：{Pattern}", RegexPattern);
            return false;
        }

        // 所有规则都匹配
        return true;
    }
}
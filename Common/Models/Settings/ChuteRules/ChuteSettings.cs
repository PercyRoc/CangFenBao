using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Common.Services.Settings;
using Prism.Mvvm;
using Serilog;

namespace Common.Models.Settings.ChuteRules;

[Configuration("ChuteSettings")]
public class ChuteSettings : BindableBase
{
    private int _chuteCount = 1;
    private Dictionary<int, BarcodeMatchRule> _chuteRules = new();
    private int _errorChuteNumber;

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
        if (string.IsNullOrEmpty(barcode)) return null;

        foreach (var rule in ChuteRules)
        {
            var chuteNumber = rule.Key;
            var chuteRule = rule.Value;

            // 检查是否匹配所有规则
            if (chuteRule.IsMatching(barcode)) return chuteNumber;
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
    private int _minLength;
    private string _notContains = string.Empty;
    private string _notEndsWith = string.Empty;
    private string _notStartsWith = string.Empty;
    private string _regexPattern = "(?=.*(?))";
    private string _startsWith = string.Empty;

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
    ///     检查条码是否匹配当前规则
    /// </summary>
    /// <param name="barcode">包裹条码</param>
    /// <returns>是否匹配</returns>
    public bool IsMatching(string barcode)
    {
        // 检查长度限制
        if (MinLength > 0 && barcode.Length < MinLength) return false;
        if (MaxLength > 0 && barcode.Length > MaxLength) return false;

        // 检查字符类型限制
        if (IsDigitOnly && !barcode.All(char.IsDigit)) return false;
        if (IsLetterOnly && !barcode.All(char.IsLetter)) return false;
        if (IsAlphanumeric && !barcode.All(c => char.IsLetterOrDigit(c))) return false;

        // 检查前缀和后缀
        if (!string.IsNullOrEmpty(StartsWith))
        {
            var startValues = StartsWith.Replace("，", ",")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s));
            if (!startValues.Any(start => barcode.StartsWith(start))) return false;
        }

        if (!string.IsNullOrEmpty(EndsWith))
        {
            var endValues = EndsWith.Replace("，", ",")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s));
            if (!endValues.Any(end => barcode.EndsWith(end))) return false;
        }

        // 检查不包含的前缀和后缀
        if (!string.IsNullOrEmpty(NotStartsWith))
        {
            var notStartValues = NotStartsWith.Replace("，", ",")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s));
            if (notStartValues.Any(start => barcode.StartsWith(start))) return false;
        }

        if (!string.IsNullOrEmpty(NotEndsWith))
        {
            var notEndValues = NotEndsWith.Replace("，", ",")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s));
            if (notEndValues.Any(end => barcode.EndsWith(end))) return false;
        }

        // 检查包含和不包含的字符串
        if (!string.IsNullOrEmpty(Contains))
        {
            var containValues = Contains.Replace("，", ",")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s));
            if (!containValues.Any(contain => barcode.Contains(contain))) return false;
        }

        if (!string.IsNullOrEmpty(NotContains))
        {
            var notContainValues = NotContains.Replace("，", ",")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s));
            if (notContainValues.Any(notContain => barcode.Contains(notContain))) return false;
        }

        // 检查正则表达式
        if (!string.IsNullOrEmpty(RegexPattern) && RegexPattern != "(?=.*(?))")
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
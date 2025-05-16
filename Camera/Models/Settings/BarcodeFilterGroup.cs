using Prism.Mvvm;

namespace Camera.Models.Settings;

/// <summary>
/// 代表一组条码过滤规则。
/// 用户可以定义多个这样的组。
/// </summary>
public class BarcodeFilterGroup : BindableBase
{
    private bool _isGroupEnabled = true;
    private string _groupName = "New Rule Group";
    private BarcodeLengthRule _lengthRule = new();
    private StringMatchRule _startsWithRule = new();
    private StringMatchRule _endsWithRule = new();
    private StringMatchRule _containsRule = new();
    private StringMatchRule _notContainsRule = new();
    private CharacterTypeRule _charTypeRule = new();
    private RegexRule _customRegexRule = new();

    /// <summary>
    /// 此规则组的名称
    /// </summary>
    public string GroupName
    {
        get => _groupName;
        set => SetProperty(ref _groupName, value);
    }

    /// <summary>
    /// 是否启用此规则组
    /// </summary>
    public bool IsGroupEnabled
    {
        get => _isGroupEnabled;
        set => SetProperty(ref _isGroupEnabled, value);
    }

    /// <summary>
    /// 条码长度规则
    /// </summary>
    public BarcodeLengthRule LengthRule
    {
        get => _lengthRule;
        set => SetProperty(ref _lengthRule, value);
    }

    /// <summary>
    /// 指定条码开头规则
    /// </summary>
    public StringMatchRule StartsWithRule
    {
        get => _startsWithRule;
        set => SetProperty(ref _startsWithRule, value);
    }

    /// <summary>
    /// 指定条码结尾规则
    /// </summary>
    public StringMatchRule EndsWithRule
    {
        get => _endsWithRule;
        set => SetProperty(ref _endsWithRule, value);
    }

    /// <summary>
    /// 必须包含规则
    /// </summary>
    public StringMatchRule ContainsRule
    {
        get => _containsRule;
        set => SetProperty(ref _containsRule, value);
    }

    /// <summary>
    /// 不能包含规则
    /// </summary>
    public StringMatchRule NotContainsRule
    {
        get => _notContainsRule;
        set => SetProperty(ref _notContainsRule, value);
    }

    /// <summary>
    /// 字符类型规则
    /// </summary>
    public CharacterTypeRule CharTypeRule
    {
        get => _charTypeRule;
        set => SetProperty(ref _charTypeRule, value);
    }

    /// <summary>
    /// 自定义正则表达式规则
    /// </summary>
    public RegexRule CustomRegexRule
    {
        get => _customRegexRule;
        set => SetProperty(ref _customRegexRule, value);
    }
} 
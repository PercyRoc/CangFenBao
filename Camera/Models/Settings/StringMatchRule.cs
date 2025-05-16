using Prism.Mvvm;

namespace Camera.Models.Settings;

/// <summary>
/// 字符串匹配规则设置
/// </summary>
public class StringMatchRule : BindableBase
{
    private bool _isEnabled;
    private string _pattern = string.Empty;

    /// <summary>
    /// 是否启用此规则
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    /// <summary>
    /// 匹配模式 (例如, 起始字符串, 包含字符串等)
    /// </summary>
    public string Pattern
    {
        get => _pattern;
        set => SetProperty(ref _pattern, value);
    }
} 
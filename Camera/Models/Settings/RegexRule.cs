namespace Camera.Models.Settings;

/// <summary>
/// 正则表达式规则
/// </summary>
public class RegexRule : BindableBase
{
    private bool _isEnabled;
    private string _regexPattern = string.Empty;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string RegexPattern
    {
        get => _regexPattern;
        set => SetProperty(ref _regexPattern, value);
    }
} 
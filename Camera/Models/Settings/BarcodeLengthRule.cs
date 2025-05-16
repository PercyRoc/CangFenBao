using Prism.Mvvm;

namespace Camera.Models.Settings;

/// <summary>
/// 条码长度规则
/// </summary>
public class BarcodeLengthRule : BindableBase
{
    private bool _isEnabled;
    private int? _minLength;
    private int? _maxLength;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public int? MinLength
    {
        get => _minLength;
        set => SetProperty(ref _minLength, value);
    }

    public int? MaxLength
    {
        get => _maxLength;
        set => SetProperty(ref _maxLength, value);
    }
} 
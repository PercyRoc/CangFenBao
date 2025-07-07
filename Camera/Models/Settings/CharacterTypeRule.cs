namespace Camera.Models.Settings;

/// <summary>
/// 字符类型规则
/// </summary>
public class CharacterTypeRule : BindableBase
{
    private bool _isEnabled;
    private BarcodeCharacterType _characterType = BarcodeCharacterType.Any;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public BarcodeCharacterType CharacterType
    {
        get => _characterType;
        set => SetProperty(ref _characterType, value);
    }
} 
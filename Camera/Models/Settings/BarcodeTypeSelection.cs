using Prism.Mvvm;

namespace Camera.Models.Settings;

/// <summary>
/// 代表一个可选择的条码类型及其启用状态
/// </summary>
public class BarcodeTypeSelection : BindableBase
{
    private string _name = string.Empty;
    private string _typeId = string.Empty;
    private bool _isEnabled;

    /// <summary>
    /// 条码类型的显示名称 (例如 "Code128", "QR Code")
    /// </summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>
    /// 条码类型的内部标识符 (用于服务或SDK)
    /// </summary>
    public string TypeId
    {
        get => _typeId;
        set => SetProperty(ref _typeId, value);
    }

    /// <summary>
    /// 是否启用此条码类型
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }
} 
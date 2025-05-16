using System.Collections.ObjectModel;
using Prism.Mvvm;

namespace Camera.Models.Settings;

/// <summary>
/// 条码类型设置
/// </summary>
public class BarcodeTypeSettings : BindableBase
{
    private ObservableCollection<BarcodeTypeSelection> _barcodeTypes = new();

    /// <summary>
    /// 支持的条码类型及其选择状态列表
    /// </summary>
    public ObservableCollection<BarcodeTypeSelection> BarcodeTypes
    {
        get => _barcodeTypes;
        set => SetProperty(ref _barcodeTypes, value);
    }

    // 构造函数中可以初始化一些默认支持的条码类型
    public BarcodeTypeSettings()
    {
        // 示例：可以从配置或枚举中动态加载这些
        _barcodeTypes.Add(new BarcodeTypeSelection { Name = "Code 128", TypeId = "Code128", IsEnabled = true });
        _barcodeTypes.Add(new BarcodeTypeSelection { Name = "QR Code", TypeId = "QRCode", IsEnabled = true });
        _barcodeTypes.Add(new BarcodeTypeSelection { Name = "Data Matrix", TypeId = "DataMatrix", IsEnabled = false });
        // ... 添加其他常见条码类型
    }
} 
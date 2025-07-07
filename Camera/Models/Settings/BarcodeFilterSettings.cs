using System.Collections.ObjectModel;

namespace Camera.Models.Settings;

/// <summary>
/// 条码过滤规则设置集合
/// </summary>
public class BarcodeFilterSettings : BindableBase
{
    private ObservableCollection<BarcodeFilterGroup> _ruleGroups = new();

    /// <summary>
    /// 规则组列表
    /// </summary>
    public ObservableCollection<BarcodeFilterGroup> RuleGroups
    {
        get => _ruleGroups;
        set => SetProperty(ref _ruleGroups, value);
    }
} 
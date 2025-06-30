using Common.Services.Settings;
using Common.Services.Ui;
using Prism.Mvvm;
using Serilog;
using XinJuLi.Models;

namespace XinJuLi.ViewModels.Settings;

/// <summary>
/// 分拣模式设置视图模型
/// </summary>
public class SortingModeSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private readonly INotificationService _notificationService;
    private SortingModeSettings _settings;

    public SortingModeSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;

        // 命令初始化（保留以防需要）

        // 加载设置
        LoadSettings();
    }

    /// <summary>
    /// 分拣模式设置对象（直接绑定）
    /// </summary>
    public SortingModeSettings Settings => _settings;

    /// <summary>
    /// 最后更新时间显示
    /// </summary>
    public string LastUpdatedDisplay => _settings.LastUpdated.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>
    /// 当前模式显示名称
    /// </summary>
    public string CurrentModeDisplayName => _settings.GetCurrentModeDisplayName();

    /// <summary>
    /// 选择的分拣模式详细描述
    /// </summary>
    public string SelectedModeDescription
    {
        get
        {
            return _settings.CurrentSortingMode switch
            {
                SortingMode.AreaCodeSorting => "根据包裹条码中的大区编码进行分拣，需要导入格口配置文件。适用于按地理区域分拣的场景。",
                SortingMode.ScanReviewSorting => "通过扫码复核接口验证包裹，成功/异常分别分拣到不同格口。适用于需要验证包裹合法性的场景。",
                SortingMode.AsnOrderSorting => "基于ASN单进行SKU分拣，需要选择对应的ASN订单。适用于按订单商品分拣的场景。",
                _ => "未知分拣方式"
            };
        }
    }

    /// <summary>
    /// 摆动方向详细描述
    /// </summary>
    public string PendulumDirectionDescription
    {
        get
        {
            return _settings.PendulumDirection switch
            {
                PendulumDirection.Left => "摆轮向左侧摆动，将包裹分拣到左侧格口。这是默认的摆动方向设置。",
                PendulumDirection.Right => "摆轮向右侧摆动，将包裹分拣到右侧格口。适用于需要向右分拣的场景。",
                _ => "未知摆动方向"
            };
        }
    }

    /// <summary>
    /// 当前生效设备ID显示
    /// </summary>
    public string CurrentDeviceIdDisplay => _settings.GetCurrentDeviceId();

    /// <summary>
    /// 加载设置
    /// </summary>
    private void LoadSettings()
    {
        try
        {
            _settings = _settingsService.LoadSettings<SortingModeSettings>();
            Log.Information("分拣模式设置已加载: {Mode}", _settings.CurrentSortingMode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载分拣模式设置时发生错误");
            _settings = new SortingModeSettings();
        }

        // 订阅设置对象的属性变更事件
        _settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    /// <summary>
    /// 处理设置对象属性变更
    /// </summary>
    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SortingModeSettings.CurrentSortingMode))
        {
            Log.Information("分拣模式已更改为: {Mode}", _settings.CurrentSortingMode);
            RaisePropertyChanged(nameof(SelectedModeDescription));
            RaisePropertyChanged(nameof(CurrentModeDisplayName));
            RaisePropertyChanged(nameof(CurrentDeviceIdDisplay));
        }
        else if (e.PropertyName == nameof(SortingModeSettings.PendulumDirection))
        {
            Log.Information("摆动方向已更改为: {Direction}", _settings.PendulumDirection);
            RaisePropertyChanged(nameof(PendulumDirectionDescription));
        }
        else if (e.PropertyName == nameof(SortingModeSettings.LastUpdated))
        {
            RaisePropertyChanged(nameof(LastUpdatedDisplay));
        }
        else if (e.PropertyName == nameof(SortingModeSettings.SkuSortDeviceId) ||
                 e.PropertyName == nameof(SortingModeSettings.ScanCheckDeviceId) ||
                 e.PropertyName == nameof(SortingModeSettings.RegionSortDeviceId))
        {
            Log.Information("设备ID已更改: {PropertyName} = {Value}", e.PropertyName, 
                e.PropertyName == nameof(SortingModeSettings.SkuSortDeviceId) ? _settings.SkuSortDeviceId :
                e.PropertyName == nameof(SortingModeSettings.ScanCheckDeviceId) ? _settings.ScanCheckDeviceId :
                _settings.RegionSortDeviceId);
            RaisePropertyChanged(nameof(CurrentDeviceIdDisplay));
        }
    }

    /// <summary>
    /// 保存配置（由SettingsDialog调用）
    /// </summary>
    public void SaveConfiguration()
    {
        try
        {
            Log.Information("保存分拣模式设置: {SelectedMode}, 摆动方向: {PendulumDirection}", 
                _settings.CurrentSortingMode, _settings.PendulumDirection);

            // 更新时间戳
            _settings.LastUpdated = DateTime.Now;

            // 验证设置
            if (!_settings.IsValid())
            {
                Log.Warning("分拣模式设置无效: {Mode}, {Direction}", 
                    _settings.CurrentSortingMode, _settings.PendulumDirection);
                throw new InvalidOperationException("设置无效，请检查选择的分拣模式和摆动方向");
            }

            // 保存设置
            _settingsService.SaveSettings(_settings);

            Log.Information("分拣模式设置保存成功: {Mode}, 摆动方向: {Direction}", 
                _settings.CurrentSortingMode, _settings.PendulumDirection);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存分拣模式设置时发生错误");
            throw;
        }
    }
} 
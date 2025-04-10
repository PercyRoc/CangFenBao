using System.Collections.ObjectModel;
using Common.Services.Settings;
using Prism.Mvvm;

namespace PlateTurnoverMachine.Models;

/// <summary>
///     翻板机配置
/// </summary>
[Configuration("PlateTurnoverSettings")]
public class PlateTurnoverSettings : BindableBase
{
    private double _defaultInterval = 200; // 默认间隔时间（毫秒）
    private int _errorChute; // 异常格口号
    private ObservableCollection<PlateTurnoverItem> _items = [];
    private string _triggerPhotoelectricIp = "192.168.1.100"; // 默认IP地址
    private int _triggerPhotoelectricPort = 2000; // 默认端口号
    private int _chuteCount = 1; // 格口总数

    /// <summary>
    ///     配置变更事件
    /// </summary>
    public event EventHandler? SettingsChanged;

    /// <summary>
    ///     格口总数
    /// </summary>
    public int ChuteCount
    {
        get => _chuteCount;
        set
        {
            if (SetProperty(ref _chuteCount, value))
            {
                OnSettingsChanged();
            }
        }
    }

    /// <summary>
    ///     翻板机配置项列表
    /// </summary>
    public ObservableCollection<PlateTurnoverItem> Items
    {
        get => _items;
        internal set
        {
            if (!SetProperty(ref _items, value)) return;
            RaisePropertyChanged();
            OnSettingsChanged();
        }
    }

    /// <summary>
    ///     触发光电IP地址
    /// </summary>
    public string TriggerPhotoelectricIp
    {
        get => _triggerPhotoelectricIp;
        set
        {
            if (SetProperty(ref _triggerPhotoelectricIp, value))
            {
                OnSettingsChanged();
            }
        }
    }

    /// <summary>
    ///     触发光电端口号
    /// </summary>
    public int TriggerPhotoelectricPort
    {
        get => _triggerPhotoelectricPort;
        set
        {
            if (SetProperty(ref _triggerPhotoelectricPort, value))
            {
                OnSettingsChanged();
            }
        }
    }

    /// <summary>
    ///     默认间隔时间（毫秒）
    /// </summary>
    public double DefaultInterval
    {
        get => _defaultInterval;
        set
        {
            if (SetProperty(ref _defaultInterval, value))
            {
                OnSettingsChanged();
            }
        }
    }

    /// <summary>
    ///     异常格口号
    /// </summary>
    public int ErrorChute
    {
        get => _errorChute;
        set
        {
            if (SetProperty(ref _errorChute, value))
            {
                OnSettingsChanged();
            }
        }
    }

    /// <summary>
    ///     中通API地址
    /// </summary>
    public string ZtoApiUrl { get; set; } = "https://intelligent-2nd-pro.zt-express.com/branchweb/sortservice";

    /// <summary>
    ///     中通公司ID
    /// </summary>
    public string ZtoCompanyId { get; set; } = string.Empty;

    /// <summary>
    ///     中通密钥
    /// </summary>
    public string ZtoSecretKey { get; set; } = string.Empty;

    /// <summary>
    ///     中通分拣线编码
    /// </summary>
    public string ZtoPipelineCode { get; set; } = string.Empty;

    /// <summary>
    ///     中通小车编码
    /// </summary>
    public string ZtoTrayCode { get; set; } = string.Empty;
    
    /// <summary>
    ///     已配置的最大光电触发次数（即最远格口的距离）
    /// </summary>
    public int MaxConfiguredDistance
    {
        get
        {
            return Items.Count == 0 ? 0 : (int)Items.Max(static item => item.Distance);
        }
    }

    /// <summary>
    ///     触发配置变更事件
    /// </summary>
    internal void OnSettingsChanged()
    {
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
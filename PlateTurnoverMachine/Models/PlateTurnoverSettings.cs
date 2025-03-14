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

    /// <summary>
    ///     翻板机配置项列表
    /// </summary>
    public ObservableCollection<PlateTurnoverItem> Items
    {
        get => _items;
        internal set
        {
            if (SetProperty(ref _items, value)) RaisePropertyChanged();
        }
    }

    /// <summary>
    ///     触发光电IP地址
    /// </summary>
    public string TriggerPhotoelectricIp
    {
        get => _triggerPhotoelectricIp;
        set => SetProperty(ref _triggerPhotoelectricIp, value);
    }

    /// <summary>
    ///     触发光电端口号
    /// </summary>
    public int TriggerPhotoelectricPort
    {
        get => _triggerPhotoelectricPort;
        set => SetProperty(ref _triggerPhotoelectricPort, value);
    }

    /// <summary>
    ///     默认间隔时间（毫秒）
    /// </summary>
    public double DefaultInterval
    {
        get => _defaultInterval;
        set => SetProperty(ref _defaultInterval, value);
    }

    /// <summary>
    ///     异常格口号
    /// </summary>
    public int ErrorChute
    {
        get => _errorChute;
        set => SetProperty(ref _errorChute, value);
    }
}
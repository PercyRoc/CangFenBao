using System.Collections.ObjectModel;
using CommonLibrary.Models.Settings;
using Prism.Mvvm;

namespace Presentation_PlateTurnoverMachine.Models;

/// <summary>
/// 翻板机配置
/// </summary>
[Configuration("PlateTurnoverSettings")]
public class PlateTurnoverSettings : BindableBase
{
    private ObservableCollection<PlateTurnoverItem> _items = [];
    private string _triggerPhotoelectricIp = "192.168.1.100"; // 默认IP地址
    private int _triggerPhotoelectricPort = 2000; // 默认端口号
    
    /// <summary>
    /// 翻板机配置项列表
    /// </summary>
    public ObservableCollection<PlateTurnoverItem> Items
    {
        get => _items;
        set
        {
            if (SetProperty(ref _items, value))
            {
                RaisePropertyChanged();
            }
        }
    }
    
    /// <summary>
    /// 触发光电IP地址
    /// </summary>
    public string TriggerPhotoelectricIp
    {
        get => _triggerPhotoelectricIp;
        set => SetProperty(ref _triggerPhotoelectricIp, value);
    }
    
    /// <summary>
    /// 触发光电端口号
    /// </summary>
    public int TriggerPhotoelectricPort
    {
        get => _triggerPhotoelectricPort;
        set => SetProperty(ref _triggerPhotoelectricPort, value);
    }
} 
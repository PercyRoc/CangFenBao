using System.Collections.ObjectModel;
using Common.Services.Settings;

namespace SortingServices.Car.Models;

/// <summary>
///     小车配置模型
/// </summary>
[Configuration("CarConfig")]
public class CarConfigModel : BindableBase
{
    private ObservableCollection<CarConfig>? _carConfigs;

    public CarConfigModel()
    {
        CarConfigs = [];
    }

    /// <summary>
    ///     小车配置集合
    /// </summary>
    public ObservableCollection<CarConfig>? CarConfigs
    {
        get => _carConfigs;
        set => SetProperty(ref _carConfigs, value);
    }
}
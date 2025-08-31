using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Common.Models.Settings.ChuteRules;
using Common.Services.Settings;
using Prism.Commands;
using Prism.Mvvm;
using SortingServices.Car.Models;

namespace SharedUI.ViewModels;

/// <summary>
///     小车分拣序列配置视图模型
/// </summary>
public class CarSequenceViewModel : BindableBase
{
    private readonly CarConfigModel _carConfigModel;
    private readonly CarSequenceSettings _carSequenceSettings;
    private readonly ISettingsService _settingsService;
    private CarSequenceItem? _selectedCarItem;

    private ChuteCarSequence? _selectedChute;

    public CarSequenceViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;

        // 加载格口设置
        var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();
        var chuteCount = chuteSettings.ChuteCount;

        // 加载小车配置
        _carConfigModel = _settingsService.LoadSettings<CarConfigModel>();

        // 加载小车序列设置
        _carSequenceSettings = _settingsService.LoadSettings<CarSequenceSettings>();

        // 初始化格口序列
        _carSequenceSettings.InitializeChutes(chuteCount);

        // 初始化命令
        AddCarCommand = new DelegateCommand<CarConfig>(AddCar, CanAddCar);
        RemoveCarCommand = new DelegateCommand(RemoveCar, CanRemoveCar)
            .ObservesProperty(() => SelectedCarItem);
        SetForwardCommand = new DelegateCommand(SetForward, CanSetDirection)
            .ObservesProperty(() => SelectedCarItem);
        SetReverseCommand = new DelegateCommand(SetReverse, CanSetDirection)
            .ObservesProperty(() => SelectedCarItem);

        // 选择第一个格口
        if (ChuteSequences.Count > 0) SelectedChute = ChuteSequences[0]; // 这会触发 set 访问器中的订阅逻辑
    }

    /// <summary>
    ///     格口集合
    /// </summary>
    public ObservableCollection<ChuteCarSequence> ChuteSequences => _carSequenceSettings.ChuteSequences;

    /// <summary>
    ///     可用小车配置
    /// </summary>
    public ObservableCollection<CarConfig>? AvailableCars => _carConfigModel.CarConfigs;

    /// <summary>
    ///     选中的格口
    /// </summary>
    public ChuteCarSequence? SelectedChute
    {
        get => _selectedChute;
        set
        {
            // 取消订阅旧序列项的事件
            UnsubscribeFromSequenceItems();

            if (SetProperty(ref _selectedChute, value))
            {
                RaisePropertyChanged(nameof(CarSequence));
                // 订阅新序列项的事件
                SubscribeToSequenceItems();
            }
        }
    }

    /// <summary>
    ///     选中的小车序列项
    /// </summary>
    public CarSequenceItem? SelectedCarItem
    {
        get => _selectedCarItem;
        set => SetProperty(ref _selectedCarItem, value);
    }

    /// <summary>
    ///     当前选中格口的小车序列
    /// </summary>
    public ObservableCollection<CarSequenceItem>? CarSequence => _selectedChute?.CarSequence;

    /// <summary>
    ///     添加小车命令
    /// </summary>
    public ICommand AddCarCommand { get; }

    /// <summary>
    ///     删除小车命令
    /// </summary>
    public ICommand RemoveCarCommand { get; }

    /// <summary>
    ///     小车正转命令
    /// </summary>
    public ICommand SetForwardCommand { get; }

    /// <summary>
    ///     小车反转命令
    /// </summary>
    public ICommand SetReverseCommand { get; }

    /// <summary>
    ///     添加小车到当前格口序列
    /// </summary>
    private void AddCar(CarConfig? car)
    {
        if (SelectedChute == null || car == null) return;

        // 创建新的小车序列项
        var carItem = new CarSequenceItem
        {
            CarAddress = car.Address,
            CarName = car.Name,
            IsReverse = false,
            DelayMs = 0 // 初始化延迟时间为0
        };

        // 添加到当前格口序列
        // ObservableCollection 的 CollectionChanged 事件会自动处理订阅/取消订阅
        SelectedChute.CarSequence.Add(carItem);

        // 选中新添加的项
        SelectedCarItem = carItem;

        // 保存设置
        SaveSettings();
    }

    /// <summary>
    ///     判断是否可以添加小车
    /// </summary>
    private bool CanAddCar(CarConfig? car)
    {
        return SelectedChute != null && car != null;
    }

    /// <summary>
    ///     从当前格口序列移除选中的小车
    /// </summary>
    private void RemoveCar()
    {
        if (SelectedChute == null || SelectedCarItem == null) return;

        // 移除选中的小车
        // ObservableCollection 的 CollectionChanged 事件会自动处理订阅/取消订阅
        SelectedChute.CarSequence.Remove(SelectedCarItem);

        // 选择下一个项
        if (SelectedChute.CarSequence.Count > 0)
            SelectedCarItem = SelectedChute.CarSequence[0];
        else
            SelectedCarItem = null;

        // 保存设置
        SaveSettings();
    }

    /// <summary>
    ///     判断是否可以移除小车
    /// </summary>
    private bool CanRemoveCar()
    {
        return SelectedChute != null && SelectedCarItem != null;
    }

    /// <summary>
    ///     设置选中小车为正转
    /// </summary>
    private void SetForward()
    {
        if (SelectedCarItem == null) return;

        SelectedCarItem.IsReverse = false;
        // PropertyChanged 事件会触发保存
        // SaveSettings(); // 不再需要显式调用
    }

    /// <summary>
    ///     设置选中小车为反转
    /// </summary>
    private void SetReverse()
    {
        if (SelectedCarItem == null) return;

        SelectedCarItem.IsReverse = true;
        // PropertyChanged 事件会触发保存
        // SaveSettings(); // 不再需要显式调用
    }

    /// <summary>
    ///     判断是否可以设置方向
    /// </summary>
    private bool CanSetDirection()
    {
        return SelectedCarItem != null;
    }

    /// <summary>
    ///     保存设置
    /// </summary>
    private void SaveSettings()
    {
        _settingsService.SaveSettings(_carSequenceSettings);
    }

    /// <summary>
    ///     订阅当前选中序列中所有项的 PropertyChanged 事件
    /// </summary>
    private void SubscribeToSequenceItems()
    {
        if (CarSequence == null) return;

        // 订阅集合变化事件，以便在添加/删除项时更新订阅
        CarSequence.CollectionChanged += OnCarSequenceCollectionChanged;

        // 订阅现有项的事件
        foreach (var item in CarSequence) item.PropertyChanged += OnCarSequenceItemPropertyChanged;
    }

    /// <summary>
    ///     取消订阅当前选中序列中所有项的 PropertyChanged 事件
    /// </summary>
    private void UnsubscribeFromSequenceItems()
    {
        if (CarSequence == null) return;

        // 取消订阅集合变化事件
        CarSequence.CollectionChanged -= OnCarSequenceCollectionChanged;

        // 取消订阅现有项的事件
        foreach (var item in CarSequence) item.PropertyChanged -= OnCarSequenceItemPropertyChanged;
    }

    /// <summary>
    ///     处理小车序列集合变化事件
    /// </summary>
    private void OnCarSequenceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 当有新项添加时，订阅其 PropertyChanged 事件
        if (e.NewItems != null)
            foreach (var item in e.NewItems.OfType<CarSequenceItem>())
                item.PropertyChanged += OnCarSequenceItemPropertyChanged;

        // 当有旧项移除时，取消订阅其 PropertyChanged 事件
        if (e.OldItems != null)
            foreach (var item in e.OldItems.OfType<CarSequenceItem>())
                item.PropertyChanged -= OnCarSequenceItemPropertyChanged;
    }

    /// <summary>
    ///     处理 CarSequenceItem 的 PropertyChanged 事件
    /// </summary>
    private void OnCarSequenceItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 只在 DelayMs 或 IsReverse 属性改变时保存
        if (e.PropertyName == nameof(CarSequenceItem.DelayMs) ||
            e.PropertyName == nameof(CarSequenceItem.IsReverse)) SaveSettings();
    }
}
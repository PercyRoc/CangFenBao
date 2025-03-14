using System.Collections.ObjectModel;
using Common.Services.Settings;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using SangNeng.Events;

namespace Presentation_SangNeng.ViewModels.Settings;

public class PalletModel : BindableBase
{
    private double _height;
    private double _length;
    private string _name = string.Empty;
    private double _weight;
    private double _width;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public double Weight
    {
        get => _weight;
        set => SetProperty(ref _weight, value);
    }

    public double Length
    {
        get => _length;
        set => SetProperty(ref _length, value);
    }

    public double Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    public double Height
    {
        get => _height;
        set => SetProperty(ref _height, value);
    }
}

[Configuration("PalletSettings")]
public class PalletSettings
{
    public ObservableCollection<PalletModel> Pallets { get; set; } = [];
}

public class PalletSettingsViewModel : BindableBase
{
    private readonly IEventAggregator _eventAggregator;
    private readonly ISettingsService _settingsService;
    private PalletSettings? _configuration;
    private ObservableCollection<PalletModel> _pallets = [];

    public PalletSettingsViewModel(ISettingsService settingsService, IEventAggregator eventAggregator)
    {
        _settingsService = settingsService;
        _eventAggregator = eventAggregator;

        AddPalletCommand = new DelegateCommand(ExecuteAddPallet);
        RemovePalletCommand = new DelegateCommand<PalletModel>(ExecuteRemovePallet);
        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveSettings);

        LoadSettings();
    }

    private PalletSettings? Configuration
    {
        get => _configuration;
        set
        {
            if (SetProperty(ref _configuration, value)) Pallets = value?.Pallets ?? [];
        }
    }

    public ObservableCollection<PalletModel> Pallets
    {
        get => _pallets;
        private set
        {
            if (!SetProperty(ref _pallets, value)) return;
            if (Configuration != null) Configuration.Pallets = value;
            RaisePropertyChanged();
        }
    }

    public DelegateCommand AddPalletCommand { get; }
    public DelegateCommand<PalletModel> RemovePalletCommand { get; }
    public DelegateCommand SaveConfigurationCommand { get; }

    private void LoadSettings()
    {
        try
        {
            Configuration = _settingsService.LoadSettings<PalletSettings>();
            if (Configuration != null) return;
            Configuration = new PalletSettings
            {
                Pallets = []
            };
            ExecuteAddPallet(); // Add a default pallet
        }
        catch (Exception)
        {
            Configuration = new PalletSettings
            {
                Pallets = []
            };
            ExecuteAddPallet(); // Add a default pallet
        }
    }

    private void ExecuteSaveSettings()
    {
        if (Configuration == null) return;

        try
        {
            Configuration.Pallets = new ObservableCollection<PalletModel>(Pallets);
            _settingsService.SaveSettings(Configuration);

            // 发布事件通知配置已更改
            _eventAggregator.GetEvent<PalletSettingsChangedEvent>().Publish();
        }
        catch (Exception)
        {
            // 处理保存失败的情况
        }
    }

    private void ExecuteAddPallet()
    {
        var newPallet = new PalletModel
        {
            Name = $"Pallet {Pallets.Count + 1}",
            Weight = 0,
            Length = 0,
            Width = 0,
            Height = 0
        };

        Pallets.Add(newPallet);
    }

    private void ExecuteRemovePallet(PalletModel pallet)
    {
        if (!Pallets.Contains(pallet)) return;
        Pallets.Remove(pallet);
    }
}
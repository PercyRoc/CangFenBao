using System.Collections.ObjectModel;
using CommonLibrary.Models.Settings;
using CommonLibrary.Services;
using Presentation_CommonLibrary.Services;
using Presentation_SangNeng.Events;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Serilog;

namespace Presentation_SangNeng.ViewModels.Settings;

public class TrayModel : BindableBase
{
    private string _name = string.Empty;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    private double _weight;

    public double Weight
    {
        get => _weight;
        set => SetProperty(ref _weight, value);
    }

    private double _length;

    public double Length
    {
        get => _length;
        set => SetProperty(ref _length, value);
    }

    private double _width;

    public double Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    private double _height;

    public double Height
    {
        get => _height;
        set => SetProperty(ref _height, value);
    }
}

[Configuration("TraySettings")]
public class TraySettings
{
    public ObservableCollection<TrayModel> Trays { get; set; } = [];
}

public class TraySettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private readonly IEventAggregator _eventAggregator;
    private TraySettings? _configuration;
    private ObservableCollection<TrayModel> _trays = [];

    public TraySettingsViewModel(
        ISettingsService settingsService,
        IEventAggregator eventAggregator)
    {
        _settingsService = settingsService;
        _eventAggregator = eventAggregator;
        
        AddTrayCommand = new DelegateCommand(ExecuteAddTray);
        RemoveTrayCommand = new DelegateCommand<TrayModel>(ExecuteRemoveTray);
        SaveSettingsCommand = new DelegateCommand(ExecuteSaveSettings);

        // Load configuration
        LoadSettings();
    }

    private TraySettings? Configuration
    {
        get => _configuration;
        set
        {
            if (SetProperty(ref _configuration, value))
            {
                Trays = value?.Trays ?? new ObservableCollection<TrayModel>();
            }
        }
    }

    public ObservableCollection<TrayModel> Trays
    {
        get => _trays;
        set
        {
            if (SetProperty(ref _trays, value))
            {
                if (Configuration != null)
                {
                    Configuration.Trays = value;
                }
                RaisePropertyChanged();
            }
        }
    }

    public DelegateCommand AddTrayCommand { get; }
    public DelegateCommand<TrayModel> RemoveTrayCommand { get; }
    public DelegateCommand SaveSettingsCommand { get; }

    private void ExecuteAddTray()
    {
        var newTray = new TrayModel
        {
            Name = $"Tray {Trays.Count + 1}",
            Weight = 0,
            Length = 0,
            Width = 0,
            Height = 0
        };
        Trays.Add(newTray);
    }

    private void ExecuteRemoveTray(TrayModel tray)
    {
        Trays.Remove(tray);
    }

    private void ExecuteSaveSettings()
    {
        if (Configuration == null) return;
        
        try
        {
            Configuration.Trays = new ObservableCollection<TrayModel>(Trays);
            _settingsService.SaveConfiguration(Configuration);
            Log.Information("托盘配置保存成功");
            
            // 发布事件通知配置已更改
            _eventAggregator.GetEvent<TraySettingsChangedEvent>().Publish();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存托盘配置失败");
        }
    }

    private void LoadSettings()
    {
        try
        {
            Configuration = _settingsService.LoadConfiguration<TraySettings>();
            if (Configuration == null)
            {
                Configuration = new TraySettings
                {
                    Trays = new ObservableCollection<TrayModel>()
                };
                ExecuteAddTray(); // Add a default tray
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载托盘配置失败");
            Configuration = new TraySettings
            {
                Trays = new ObservableCollection<TrayModel>()
            };
            ExecuteAddTray(); // Add a default tray
        }
    }
}
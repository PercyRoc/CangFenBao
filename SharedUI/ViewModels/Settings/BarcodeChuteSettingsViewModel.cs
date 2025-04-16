using System.Collections.ObjectModel;
using System.Windows;
using Common.Models.Settings.ChuteRules;
using Common.Services.Settings;
using Prism.Commands;
using Prism.Mvvm;

namespace SharedUI.ViewModels.Settings;

public class BarcodeChuteSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private ObservableCollection<int> _chuteNumbers = [];
    private ChuteSettings _configuration = new();
    private BarcodeMatchRule _currentRule = new();
    private int _selectedChuteNumber = 1;

    public BarcodeChuteSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);

        // 加载配置
        LoadSettings();

        // 监听格口数量变化
        Configuration.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Configuration.ChuteCount)) UpdateChuteNumbers();
        };
    }

    public DelegateCommand SaveConfigurationCommand { get; }

    public ChuteSettings Configuration
    {
        get => _configuration;
        private set => SetProperty(ref _configuration, value);
    }

    public int SelectedChuteNumber
    {
        get => _selectedChuteNumber;
        set
        {
            if (SetProperty(ref _selectedChuteNumber, value)) LoadChuteRule(value);
        }
    }

    public ObservableCollection<int> ChuteNumbers
    {
        get => _chuteNumbers;
        set => SetProperty(ref _chuteNumbers, value);
    }

    public BarcodeMatchRule CurrentRule
    {
        get => _currentRule;
        private set => SetProperty(ref _currentRule, value);
    }

    private void LoadSettings()
    {
        Configuration = _settingsService.LoadSettings<ChuteSettings>();
        UpdateChuteNumbers();

        if (Configuration.ChuteRules.TryGetValue(SelectedChuteNumber, out var rule))
        {
            CurrentRule = rule;
        }
        else
        {
            CurrentRule = new BarcodeMatchRule();
            Configuration.ChuteRules[SelectedChuteNumber] = CurrentRule;
        }
    }

    private void UpdateChuteNumbers()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(UpdateChuteNumbers);
            return;
        }

        ChuteNumbers.Clear();
        for (var i = 1; i <= Configuration.ChuteCount; i++) ChuteNumbers.Add(i);

        if (SelectedChuteNumber > Configuration.ChuteCount)
        {
            SelectedChuteNumber = 1;
        }
    }

    private void LoadChuteRule(int chuteNumber)
    {
        if (Configuration.ChuteRules.TryGetValue(chuteNumber, out var rule))
        {
            CurrentRule = rule;
        }
        else
        {
            CurrentRule = new BarcodeMatchRule();
            Configuration.ChuteRules[chuteNumber] = CurrentRule;
        }
    }

    private void ExecuteSaveConfiguration()
    {
        // 确保当前规则已保存到字典中
        Configuration.ChuteRules[SelectedChuteNumber] = CurrentRule;

        // 保存配置
        _settingsService.SaveSettings(Configuration, true);
    }
}
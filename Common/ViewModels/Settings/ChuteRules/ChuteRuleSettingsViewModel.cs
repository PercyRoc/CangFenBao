using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Input;
using Common.Models.Settings.ChuteRules;
using Common.Services.Settings;
using Common.Services.Ui;
using Serilog;

namespace Common.ViewModels.Settings.ChuteRules
{
    public class ChuteRuleSettingsViewModel : BindableBase
    {
        private readonly ISettingsService _settingsService;
        private readonly INotificationService _notificationService;
        private ChuteSettings _chuteSettings = null!;
        private int _currentChuteNumber = 1;
        private BarcodeMatchRule _currentRule = null!;

        public ChuteSettings ChuteSettings
        {
            get => _chuteSettings;
            set
            {
                if (SetProperty(ref _chuteSettings, value))
                {
                    UpdateChuteNumberOptions();
                }
            }
        }

        public int CurrentChuteNumber
        {
            get => _currentChuteNumber;
            set
            {
                if (SetProperty(ref _currentChuteNumber, value))
                {
                    LoadCurrentRule();
                }
            }
        }

        public BarcodeMatchRule CurrentRule
        {
            get => _currentRule;
            set => SetProperty(ref _currentRule, value);
        }

        public ObservableCollection<int> ChuteNumberOptions { get; set; } = [];

        private ObservableCollection<EditableChuteRuleItem> ChuteRuleItems { get; set; } = null!;

        public ICommand SaveSettingsCommand { get; }

        public ChuteRuleSettingsViewModel(ISettingsService settingsService, INotificationService notificationService)
        {
            _settingsService = settingsService;
            _notificationService = notificationService;

            LoadChuteSettings();

            SaveSettingsCommand = new DelegateCommand(SaveChuteSettings);

            // 监听ChuteSettings的ChuteCount属性变化
            PropertyChanged += OnPropertyChanged;
        }

        private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChuteSettings))
            {
                ChuteSettings.PropertyChanged += OnChuteSettingsPropertyChanged;
            }
        }

        private void OnChuteSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChuteSettings.ChuteCount))
            {
                UpdateChuteNumberOptions();
            }
        }

        private void LoadChuteSettings()
        {
            ChuteSettings = _settingsService.LoadSettings<ChuteSettings>();
            ChuteSettings.PropertyChanged += OnChuteSettingsPropertyChanged;
            var items = ChuteSettings.ChuteRules.Select(kvp => new EditableChuteRuleItem(kvp.Key, kvp.Value))
                                              .OrderBy(item => item.ChuteNumber);
            ChuteRuleItems = new ObservableCollection<EditableChuteRuleItem>(items);
            UpdateChuteNumberOptions();
            LoadCurrentRule();
        }

        private void UpdateChuteNumberOptions()
        {
            ChuteNumberOptions.Clear();
            var chuteCount = ChuteSettings.ChuteCount;
            for (int i = 1; i <= chuteCount; i++)
            {
                ChuteNumberOptions.Add(i);
            }
            
            // 确保当前选择的格口号在有效范围内
            if (CurrentChuteNumber > chuteCount)
            {
                CurrentChuteNumber = 1;
            }
        }

        private void LoadCurrentRule()
        {
            if (ChuteSettings.ChuteRules.TryGetValue(CurrentChuteNumber, out var value))
            {
                CurrentRule = value;
            }
            else
            {
                CurrentRule = new BarcodeMatchRule();
                ChuteSettings.ChuteRules[CurrentChuteNumber] = CurrentRule;
            }
        }

        private void SaveChuteSettings()
        {
            try
            {
                // Save current rule before saving settings
                ChuteSettings.ChuteRules[CurrentChuteNumber] = CurrentRule;

                // Update ChuteSettings.ChuteRules from ChuteRuleItems before saving
                foreach (var item in ChuteRuleItems)
                {
                    ChuteSettings.ChuteRules[item.ChuteNumber] = item.Rule;
                }

                _settingsService.SaveSettings(ChuteSettings, true, true);
                _notificationService.ShowSuccess("格口规则配置已保存。");
                Log.Information("格口规则配置已保存。");
            }
            catch (ValidationException vex)
            {
                _notificationService.ShowError($"保存格口规则配置失败: {vex.Message}");
                Log.Error(vex, "保存格口规则配置时发生验证错误。");
            }
            catch (Exception ex)
            {
                _notificationService.ShowError("保存格口规则配置失败，请查看日志。");
                Log.Error(ex, "保存格口规则配置时发生未知错误。");
            }
        }
    }
} 
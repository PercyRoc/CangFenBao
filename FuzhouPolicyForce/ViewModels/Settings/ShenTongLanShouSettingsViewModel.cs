using Common.Services.Settings;
using FuzhouPolicyForce.Models;

namespace FuzhouPolicyForce.ViewModels.Settings
{
    public class ShenTongLanShouSettingsViewModel : BindableBase
    {
        private readonly ISettingsService _settingsService;
        private ShenTongLanShouConfig _config;

        public ShenTongLanShouConfig Config
        {
            get => _config;
            set => SetProperty(ref _config, value);
        }

        public DelegateCommand SaveCommand { get; }

        public ShenTongLanShouSettingsViewModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;
            _config = _settingsService.LoadSettings<ShenTongLanShouConfig>();
            SaveCommand = new DelegateCommand(Save);
        }

        private void Save()
        {
            _settingsService.SaveSettings(Config, validate: false);
        }
    }
} 
using Common.Services.Settings;
using HandyControl.Controls;
using JinHuaQiHang.Models.Settings;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using System.Windows.Input;

namespace JinHuaQiHang.ViewModels.Settings
{
    public class YunDaUploadSettingsViewModel : BindableBase
    {
        private readonly ISettingsService _settingsService;
        private YunDaUploadSettings _settings;

        public YunDaUploadSettings Settings
        {
            get => _settings;
            set => SetProperty(ref _settings, value);
        }

        public ICommand SaveCommand { get; }

        public YunDaUploadSettingsViewModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;
            LoadSettings();
            SaveCommand = new DelegateCommand(SaveSettings);
        }

        private void LoadSettings()
        {
            Settings = _settingsService.LoadSettings<YunDaUploadSettings>();
            Log.Information("金华启航韵达揽收配置加载成功。");
        }

        private void SaveSettings()
        {
            _settingsService.SaveSettings(Settings);
            Growl.Success("保存成功");
            Log.Information("金华启航韵达揽收配置保存成功。");
        }
    }
} 
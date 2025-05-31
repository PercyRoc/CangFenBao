using Common.Services.Settings;
using Common.Services.Ui;
using Serilog;
using Server.YouYu.Models;

namespace Server.YouYu.ViewModels
{
    public class YouYuSettingsViewModel : BindableBase
    {
        private readonly ISettingsService _settingsService;
        private readonly INotificationService _notificationService;

        public YouYuSettingsViewModel(
            ISettingsService settingsService, 
            INotificationService notificationService)
        {
            _settingsService = settingsService;
            _notificationService = notificationService;
            Settings = _settingsService.LoadSettings<SegmentCodeUrlConfig>();
            SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);
        }

        public SegmentCodeUrlConfig Settings { get; }

        public DelegateCommand SaveConfigurationCommand { get; }

        private void ExecuteSaveConfiguration()
        {
            try
            {
                _settingsService.SaveSettings(Settings, true);
                Log.Information("右玉接口配置已保存。");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存右玉接口配置时发生错误。");
                _notificationService.ShowError("保存右玉接口配置时发生错误，详情请查看日志。");
            }
        }
    }
} 
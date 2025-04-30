using Common.Services.Settings;
using Common.Services.Ui;
using Serilog;
using XinBeiYang.Models;

namespace XinBeiYang.ViewModels.Settings;

internal class HostSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private HostConfiguration _configuration = new();

    public HostSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;

        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);

        // 加载配置
        LoadConfiguration();
    }

    internal DelegateCommand SaveConfigurationCommand { get; }

    public HostConfiguration Configuration
    {
        get => _configuration;
        private set => SetProperty(ref _configuration, value);
    }

    private void LoadConfiguration()
    {
        try
        {
            Configuration = _settingsService.LoadSettings<HostConfiguration>();
            Log.Information("主机配置已加载");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载主机配置时发生错误");
            _notificationService.ShowError("加载配置失败");
        }
    }

    private void ExecuteSaveConfiguration()
    {
        try
        {
            _settingsService.SaveSettings(Configuration);
            Log.Information("主机配置已保存");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存主机配置时发生错误");
            _notificationService.ShowError("保存配置失败");
        }
    }
}
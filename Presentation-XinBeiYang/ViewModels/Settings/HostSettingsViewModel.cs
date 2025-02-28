using CommonLibrary.Services;
using Presentation_CommonLibrary.Services;
using Presentation_XinBeiYang.Models;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;

namespace Presentation_XinBeiYang.ViewModels.Settings;

public class HostSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private readonly INotificationService _notificationService;
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

    public DelegateCommand SaveConfigurationCommand { get; }

    public HostConfiguration Configuration
    {
        get => _configuration;
        private set => SetProperty(ref _configuration, value);
    }

    private void LoadConfiguration()
    {
        try
        {
            Configuration = _settingsService.LoadConfiguration<HostConfiguration>();
            Log.Information("主机配置已加载");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载主机配置时发生错误");
            _notificationService.ShowError("加载配置失败", ex.Message);
        }
    }

    private void ExecuteSaveConfiguration()
    {
        try
        {
            _settingsService.SaveConfiguration(Configuration);
            Log.Information("主机配置已保存");
            _notificationService.ShowSuccess("配置已保存");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存主机配置时发生错误");
            _notificationService.ShowError("保存配置失败", ex.Message);
        }
    }
} 
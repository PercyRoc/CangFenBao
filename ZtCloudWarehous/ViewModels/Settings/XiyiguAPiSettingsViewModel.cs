using Common.Services.Settings;
using Serilog;

namespace ZtCloudWarehous.ViewModels.Settings;

public class XiyiguAPiSettingsViewModel: BindableBase
{
    private readonly ISettingsService _settingsService;
    private XiyiguApiSettings _xiyiguApiSettings = new();
    
    /// <summary>
    ///     API设置
    /// </summary>
    public XiyiguApiSettings XiyiguApiSettings
    {
        get => _xiyiguApiSettings;
        private set => SetProperty(ref _xiyiguApiSettings, value);
    }


    /// <summary>
    ///     构造函数
    /// </summary>
    /// <param name="settingsService">设置服务</param>
    public XiyiguAPiSettingsViewModel(
        ISettingsService settingsService)
    {
        _settingsService = settingsService;

        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);

        LoadSettings();
    }
    
    /// <summary>
    ///     保存配置命令
    /// </summary>
    internal DelegateCommand SaveConfigurationCommand { get; }

    /// <summary>
    ///     加载设置
    /// </summary>
    private void LoadSettings()
    {
        try
        {
            XiyiguApiSettings = _settingsService.LoadSettings<XiyiguApiSettings>();
            Log.Information("API设置已加载");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载API设置时发生错误");
        }
    }
    
    /// <summary>
    ///     执行保存配置
    /// </summary>
    private void ExecuteSaveConfiguration()
    {
        try
        {
            _settingsService.SaveSettings(XiyiguApiSettings);
            Log.Information("API设置已保存");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存API设置时发生错误");
        }
    }
}
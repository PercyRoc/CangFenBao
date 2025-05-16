using Common.Services.Settings;
using Common.Services.Ui;
using Serilog;
using SortingServices.Modules.Models;

namespace SharedUI.ViewModels.Settings;

/// <summary>
///     TCP设置视图模型
/// </summary>
public class ModulesTcpSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ModelsTcpSettings _settings;
    private readonly ISettingsService _settingsService;
    private string _address;
    private int _port;
    private int _minTime;
    private int _maxTime;
    private int _exceptionChute;

    /// <summary>
    ///     初始化TCP设置视图模型
    /// </summary>
    /// <param name="settingsService">设置服务</param>
    /// <param name="notificationService">通知服务</param>
    public ModulesTcpSettingsViewModel(ISettingsService settingsService, INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;

        // 加载设置
        _settings = _settingsService.LoadSettings<ModelsTcpSettings>();

        // 初始化属性
        _address = _settings.Address;
        _port = _settings.Port;
        _minTime = _settings.MinTime;
        _maxTime = _settings.MaxTime;
        _exceptionChute = _settings.ExceptionChute;

        // 初始化命令
        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveCommand);
    }

    /// <summary>
    ///     TCP地址
    /// </summary>
    public string Address
    {
        get => _address;
        set => SetProperty(ref _address, value);
    }

    /// <summary>
    ///     端口号
    /// </summary>
    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    /// <summary>
    ///     最小时间
    /// </summary>
    public int MinTime
    {
        get => _minTime;
        set => SetProperty(ref _minTime, value);
    }

    /// <summary>
    ///     最大时间
    /// </summary>
    public int MaxTime
    {
        get => _maxTime;
        set => SetProperty(ref _maxTime, value);
    }

    /// <summary>
    ///     异常格口
    /// </summary>
    public int ExceptionChute
    {
        get => _exceptionChute;
        set => SetProperty(ref _exceptionChute, value);
    }

    /// <summary>
    ///     保存配置命令
    /// </summary>
    public DelegateCommand SaveConfigurationCommand { get; }

    /// <summary>
    ///     执行保存命令
    /// </summary>
    private void ExecuteSaveCommand()
    {
        try
        {
            // 更新设置
            _settings.Address = _address;
            _settings.Port = _port;
            _settings.MinTime = _minTime;
            _settings.MaxTime = _maxTime;
            _settings.ExceptionChute = _exceptionChute;

            _settingsService.SaveSettings(_settings, true);
            Log.Information("TCP设置已保存: {Address}:{Port}, MinTime: {MinTime}, MaxTime: {MaxTime}, ExceptionChute: {ExceptionChute}", 
                            _address, _port, _minTime, _maxTime, _exceptionChute);
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"保存设置时发生错误: {ex.Message}");
            Log.Error(ex, "保存TCP设置时发生错误");
        }
    }
}
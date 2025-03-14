using Common.Services.Settings;
using Common.Services.Ui;
using Modules.Models;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;

namespace Modules.ViewModels;

/// <summary>
///     TCP设置视图模型
/// </summary>
internal class TcpSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly TcpSettings _settings;
    private readonly ISettingsService _settingsService;
    private string _address;
    private int _port;

    /// <summary>
    ///     初始化TCP设置视图模型
    /// </summary>
    /// <param name="settingsService">设置服务</param>
    /// <param name="notificationService">通知服务</param>
    public TcpSettingsViewModel(ISettingsService settingsService, INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;

        // 加载设置
        _settings = _settingsService.LoadSettings<TcpSettings>();

        // 初始化属性
        _address = _settings.Address;
        _port = _settings.Port;

        // 初始化命令
        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveCommand);

        // 注册设置变更回调
        _settingsService.OnSettingsChanged<TcpSettings>(OnSettingsChanged);
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

            // 保存设置
            var validationResults = _settingsService.SaveSettings(_settings, true);

            if (validationResults.Length > 0)
            {
                var errorMessage = string.Join("\n", validationResults.Select(static r => r.ErrorMessage));
                _notificationService.ShowError($"保存设置失败：\n{errorMessage}");
                Log.Error("保存TCP设置失败: {ErrorMessage}", errorMessage);
                return;
            }

            Log.Information("TCP设置已保存: {Address}:{Port}", _address, _port);
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"保存设置时发生错误: {ex.Message}");
            Log.Error(ex, "保存TCP设置时发生错误");
        }
    }

    /// <summary>
    ///     处理设置变更
    /// </summary>
    /// <param name="settings">新的设置</param>
    private void OnSettingsChanged(TcpSettings settings)
    {
        Address = settings.Address;
        Port = settings.Port;
    }
}
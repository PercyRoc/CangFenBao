using Common.Services.Settings;
using WeiCiModule.Models.Settings;
using Serilog;

namespace WeiCiModule.ViewModels.Settings;

/// <summary>
/// 模组带TCP设置ViewModel
/// </summary>
public class ModulesTcpSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private ModelsTcpSettings _settings;

    public ModulesTcpSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        _settings = new ModelsTcpSettings();
        
        LoadConfiguration();

        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);
        ResetConfigurationCommand = new DelegateCommand(ExecuteResetConfiguration);
        TestConnectionCommand = new DelegateCommand(ExecuteTestConnection);
    }

    public DelegateCommand SaveConfigurationCommand { get; }
    public DelegateCommand ResetConfigurationCommand { get; }
    public DelegateCommand TestConnectionCommand { get; }

    /// <summary>
    /// TCP服务器监听地址
    /// </summary>
    public string Address
    {
        get => _settings.Address;
        set
        {
            if (_settings.Address != value)
            {
                _settings.Address = value;
                RaisePropertyChanged();
            }
        }
    }

    /// <summary>
    /// TCP服务器监听端口
    /// </summary>
    public int Port
    {
        get => _settings.Port;
        set
        {
            if (_settings.Port != value)
            {
                _settings.Port = value;
                RaisePropertyChanged();
            }
        }
    }

    /// <summary>
    /// 最小等待时间（毫秒）
    /// </summary>
    public int MinWaitTime
    {
        get => _settings.MinWaitTime;
        set
        {
            if (_settings.MinWaitTime != value)
            {
                _settings.MinWaitTime = value;
                RaisePropertyChanged();
            }
        }
    }

    /// <summary>
    /// 最大等待时间（毫秒）
    /// </summary>
    public int MaxWaitTime
    {
        get => _settings.MaxWaitTime;
        set
        {
            if (_settings.MaxWaitTime != value)
            {
                _settings.MaxWaitTime = value;
                RaisePropertyChanged();
            }
        }
    }

    /// <summary>
    /// 异常格口号
    /// </summary>
    public int ExceptionChute
    {
        get => _settings.ExceptionChute;
        set
        {
            if (_settings.ExceptionChute != value)
            {
                _settings.ExceptionChute = value;
                RaisePropertyChanged();
            }
        }
    }

    /// <summary>
    /// 不在规则内条码的格口号
    /// </summary>
    public int NoRuleChute
    {
        get => _settings.NoRuleChute;
        set
        {
            if (_settings.NoRuleChute != value)
            {
                _settings.NoRuleChute = value;
                RaisePropertyChanged();
            }
        }
    }

    private void LoadConfiguration()
    {
        try
        {
            _settings = _settingsService.LoadSettings<ModelsTcpSettings>();
            
            // 通知所有属性已更改
            RaisePropertyChanged(nameof(Address));
            RaisePropertyChanged(nameof(Port));
            RaisePropertyChanged(nameof(MinWaitTime));
            RaisePropertyChanged(nameof(MaxWaitTime));
            RaisePropertyChanged(nameof(ExceptionChute));
            RaisePropertyChanged(nameof(NoRuleChute));

            Log.Information("模组带TCP设置已加载成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载模组带TCP设置时发生错误");
            // 使用默认设置
            _settings = new ModelsTcpSettings();
        }
    }

    public void ExecuteSaveConfiguration()
    {
        try
        {
            _settingsService.SaveSettings(_settings);
            Log.Information("模组带TCP设置已保存成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存模组带TCP设置时发生错误");
            throw;
        }
    }

    private void ExecuteResetConfiguration()
    {
        try
        {
            _settings = new ModelsTcpSettings();
            
            // 通知所有属性已更改
            RaisePropertyChanged(nameof(Address));
            RaisePropertyChanged(nameof(Port));
            RaisePropertyChanged(nameof(MinWaitTime));
            RaisePropertyChanged(nameof(MaxWaitTime));
            RaisePropertyChanged(nameof(ExceptionChute));
            RaisePropertyChanged(nameof(NoRuleChute));

            Log.Information("模组带TCP设置已重置为默认值");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "重置模组带TCP设置时发生错误");
        }
    }

    private void ExecuteTestConnection()
    {
        try
        {
            Log.Information("测试模组带TCP连接: {Address}:{Port}", Address, Port);
            // 这里可以添加连接测试逻辑
        }
        catch (Exception ex)
        {
            Log.Error(ex, "测试模组带TCP连接时发生错误");
        }
    }
} 
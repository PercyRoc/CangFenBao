using Common.Services.Settings;
using AmericanQuickHands.Models.Settings;
using AmericanQuickHands.Models.Api;
using Serilog;
using System.ComponentModel.DataAnnotations;
using Common.Services.Notifications;
using Prism.Commands;
using Prism.Mvvm;
using System.Net.Http;
using Prism.Dialogs;

namespace AmericanQuickHands.ViewModels.Settings;

/// <summary>
/// Swiftx API设置视图模型
/// </summary>
public class AmericanQuickHandsApiSettingsViewModel : BindableBase, IDialogAware
{
    private readonly ISettingsService _settingsService;
    private readonly INotificationService _notificationService;
    private AmericanQuickHandsApiSettings _settings = new();

    public AmericanQuickHandsApiSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        
        // 初始化 RequestClose 属性
        RequestClose = new DialogCloseListener();
        
        TestConnectionCommand = new DelegateCommand(ExecuteTestConnection, CanExecuteTestConnection);
        
        LoadConfiguration();
    }

    #region Properties

    /// <summary>
    /// API接口地址
    /// </summary>
    public string ApiUrl
    {
        get => _settings.ApiUrl;
        set
        {
            if (_settings.ApiUrl == value) return;
            _settings.ApiUrl = value;
            RaisePropertyChanged();
            TestConnectionCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 应用密钥
    /// </summary>
    public string AppKey
    {
        get => _settings.AppKey;
        set
        {
            if (_settings.AppKey == value) return;
            _settings.AppKey = value;
            RaisePropertyChanged();
            TestConnectionCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 应用秘钥
    /// </summary>
    public string AppSecret
    {
        get => _settings.AppSecret;
        set
        {
            if (_settings.AppSecret == value) return;
            _settings.AppSecret = value;
            RaisePropertyChanged();
            TestConnectionCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 分拣机编码
    /// </summary>
    public string SortingMachineCode
    {
        get => _settings.SortingMachineCode;
        set
        {
            if (_settings.SortingMachineCode == value) return;
            _settings.SortingMachineCode = value;
            RaisePropertyChanged();
            TestConnectionCommand.RaiseCanExecuteChanged();
        }
    }



    #endregion

    #region Commands

    /// <summary>
    /// 测试连接命令
    /// </summary>
    public DelegateCommand TestConnectionCommand { get; }

    #endregion

    #region Private Methods

    /// <summary>
    /// 加载配置
    /// </summary>
    private void LoadConfiguration()
    {
        try
        {
            _settings = _settingsService.LoadSettings<AmericanQuickHandsApiSettings>();
            
            // 通知所有属性变更
            RaisePropertyChanged(nameof(ApiUrl));
            RaisePropertyChanged(nameof(AppKey));
            RaisePropertyChanged(nameof(AppSecret));
            RaisePropertyChanged(nameof(SortingMachineCode));
            
            Log.Information("美国快手API设置加载完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载美国快手API设置时发生错误");
            _settings = new AmericanQuickHandsApiSettings();
            _notificationService.ShowError("加载API设置失败");
        }
    }



    /// <summary>
    /// 执行测试连接
    /// </summary>
    private async void ExecuteTestConnection()
    {
        try
        {
            if (!_settings.IsValid())
            {
                _notificationService.ShowWarning("请先完善API配置信息");
                return;
            }

            _notificationService.ShowSuccess("正在测试API连接...");
            
            // 创建临时的API服务实例进行测试
            var httpClient = new HttpClient();
            var apiService = new AmericanQuickHandsApiService(httpClient, _settingsService);
            
            // 调用pingPong接口测试连接
            var result = await apiService.TestConnectionAsync();
            
            if (result.Success)
            {
                _notificationService.ShowSuccess("API连接测试成功");
                Log.Information("Swiftx API连接测试成功");
            }
            else
            {
                _notificationService.ShowError($"API连接测试失败: {result.Message}");
                Log.Warning("Swiftx API连接测试失败: {ErrorMessage}", result.Message);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "测试Swiftx API连接时发生错误");
            _notificationService.ShowError($"API连接测试失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 判断是否可以测试连接
    /// </summary>
    /// <returns></returns>
    private bool CanExecuteTestConnection()
    {
        return _settings.IsValid();
    }

    #endregion

    #region IDialogAware Implementation

    public string Title => "美国快手API设置";

    public DialogCloseListener RequestClose { get; }

    public bool CanCloseDialog() => true;

    public void OnDialogClosed()
    {
        // 对话框关闭时的清理工作
    }

    public void OnDialogOpened(IDialogParameters parameters)
    {
        // 对话框打开时的初始化工作
        LoadConfiguration();
    }

    #endregion
}
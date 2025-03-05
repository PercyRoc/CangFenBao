using CommonLibrary.Services;
using Presentation_CommonLibrary.Services;
using Presentation_XinBa.Services;
using Presentation_XinBa.Services.Models;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Services.Dialogs;
using Serilog;

namespace Presentation_XinBa.ViewModels;

/// <summary>
/// 登录视图模型
/// </summary>
public class LoginViewModel : BindableBase, IDialogAware
{
    private readonly IApiService _apiService;
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private int _employeeId;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private bool _isLoggingIn;
    private string _statusMessage = string.Empty;
    private bool _rememberCredentials = true;

    /// <summary>
    /// 登录成功事件
    /// </summary>
    public event EventHandler? LoginSucceeded;

    /// <summary>
    /// 构造函数
    /// </summary>
    public LoginViewModel(
        IApiService apiService,
        INotificationService notificationService,
        ISettingsService settingsService)
    {
        _apiService = apiService;
        _notificationService = notificationService;
        _settingsService = settingsService;

        LoginCommand = new DelegateCommand(ExecuteLogin, CanExecuteLogin)
            .ObservesProperty(() => EmployeeId)
            .ObservesProperty(() => Username)
            .ObservesProperty(() => Password)
            .ObservesProperty(() => IsLoggingIn);
        CancelCommand = new DelegateCommand(ExecuteCancel);
        
        // 加载保存的凭证
        LoadSavedCredentials();
    }

    /// <summary>
    /// 员工ID
    /// </summary>
    public int EmployeeId
    {
        get => _employeeId;
        set => SetProperty(ref _employeeId, value);
    }
    
    /// <summary>
    /// API用户名
    /// </summary>
    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }
    
    /// <summary>
    /// API密码
    /// </summary>
    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }
    
    /// <summary>
    /// 记住凭证
    /// </summary>
    public bool RememberCredentials
    {
        get => _rememberCredentials;
        set => SetProperty(ref _rememberCredentials, value);
    }

    /// <summary>
    /// 是否正在登录
    /// </summary>
    public bool IsLoggingIn
    {
        get => _isLoggingIn;
        set => SetProperty(ref _isLoggingIn, value);
    }

    /// <summary>
    /// 状态消息
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// 登录命令
    /// </summary>
    public DelegateCommand LoginCommand { get; }

    /// <summary>
    /// 取消命令
    /// </summary>
    public DelegateCommand CancelCommand { get; }

    /// <summary>
    /// 对话框标题
    /// </summary>
    public string Title => "Employee Login";

    /// <summary>
    /// 请求关闭对话框事件
    /// </summary>
    public event Action<IDialogResult>? RequestClose;

    /// <summary>
    /// 是否可以关闭对话框
    /// </summary>
    public bool CanCloseDialog()
    {
        return !IsLoggingIn;
    }

    /// <summary>
    /// 对话框关闭时
    /// </summary>
    public void OnDialogClosed()
    {
    }

    /// <summary>
    /// 对话框打开时
    /// </summary>
    public void OnDialogOpened(IDialogParameters parameters)
    {
    }
    
    /// <summary>
    /// 加载保存的凭证
    /// </summary>
    private void LoadSavedCredentials()
    {
        try
        {
            var credentials = _settingsService.LoadConfiguration<ApiCredentials>();
            if (!string.IsNullOrEmpty(credentials.Username) && !string.IsNullOrEmpty(credentials.Password))
            {
                Username = credentials.Username;
                Password = credentials.Password;
                Log.Information("已加载保存的API凭证");
            }
            else
            {
                // 设置默认值
                Username = "test_dws";
                Password = "testWds";
                Log.Information("使用默认API凭证");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载API凭证失败，使用默认值");
            Username = "test_dws";
            Password = "testWds";
        }
    }

    /// <summary>
    /// 执行登录
    /// </summary>
    private async void ExecuteLogin()
    {
        try
        {
            IsLoggingIn = true;
            StatusMessage = "Logging in...";
            
            // 保存凭证
            if (RememberCredentials)
            {
                var credentials = new ApiCredentials
                {
                    Username = Username,
                    Password = Password
                };
                _settingsService.SaveConfiguration(credentials);
                Log.Information("已保存API凭证");
            }
            
            // 执行登录
            var success = await _apiService.LoginAsync(EmployeeId, Username, Password);
            
            if (success)
            {
                Log.Information("登录成功: EmployeeId={EmployeeId}", EmployeeId);
                _notificationService.ShowSuccess($"Employee {EmployeeId} logged in successfully");
                
                // 触发登录成功事件
                LoginSucceeded?.Invoke(this, EventArgs.Empty);
                
                // 注意：不再在这里关闭对话框，而是由App.xaml.cs中的OnLoginSucceeded方法负责关闭
                // 这样可以确保主窗口显示后再关闭登录窗口
                // RequestClose?.Invoke(new DialogResult(ButtonResult.OK));
            }
            else
            {
                StatusMessage = "Login failed. Please try again.";
                Log.Warning("登录失败: EmployeeId={EmployeeId}", EmployeeId);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Login error: {ex.Message}";
            Log.Error(ex, "登录过程中发生错误: EmployeeId={EmployeeId}", EmployeeId);
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    /// <summary>
    /// 是否可以执行登录
    /// </summary>
    private bool CanExecuteLogin()
    {
        return EmployeeId > 0 && 
               !string.IsNullOrWhiteSpace(Username) && 
               !string.IsNullOrWhiteSpace(Password) && 
               !IsLoggingIn;
    }

    /// <summary>
    /// 执行取消
    /// </summary>
    private void ExecuteCancel()
    {
        Log.Information("User canceled login");
        RequestClose?.Invoke(new DialogResult(ButtonResult.Cancel));
    }
} 
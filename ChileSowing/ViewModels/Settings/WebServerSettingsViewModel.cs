using ChileSowing.Models.Settings;
using ChileSowing.Services;
using Common.Services.Settings;
using Common.Services.Notifications;
using Serilog;
using System.Net.Http;
using System.Net.NetworkInformation;

namespace ChileSowing.ViewModels.Settings;

/// <summary>
/// Web服务器设置视图模型
/// </summary>
public class WebServerSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private readonly IWebServerService _webServerService;
    private readonly INotificationService _notificationService;
    private WebServerSettings _settings;
    private bool _isTesting;
    private bool _isRestarting;
    private string? _testResults;

    public WebServerSettingsViewModel(
        ISettingsService settingsService,
        IWebServerService webServerService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _webServerService = webServerService;
        _notificationService = notificationService;
        
        // 加载设置
        LoadSettings();
        
        // 初始化命令
        SaveCommand = new DelegateCommand(ExecuteSave);
        TestConnectionCommand = new DelegateCommand(ExecuteTestConnection);
        RestartServerCommand = new DelegateCommand(ExecuteRestartServer);
        ResetToDefaultCommand = new DelegateCommand(ExecuteResetToDefault);
    }

    /// <summary>
    /// Web服务器设置
    /// </summary>
    public WebServerSettings Settings
    {
        get => _settings;
        set => SetProperty(ref _settings, value);
    }

    /// <summary>
    /// 是否正在测试
    /// </summary>
    public bool IsTesting
    {
        get => _isTesting;
        set => SetProperty(ref _isTesting, value);
    }

    /// <summary>
    /// 是否正在重启服务器
    /// </summary>
    public bool IsRestarting
    {
        get => _isRestarting;
        set => SetProperty(ref _isRestarting, value);
    }

    /// <summary>
    /// 测试结果
    /// </summary>
    public string? TestResults
    {
        get => _testResults;
        set => SetProperty(ref _testResults, value);
    }

    /// <summary>
    /// 服务器运行状态
    /// </summary>
    public bool IsServerRunning => _webServerService.IsRunning;

    /// <summary>
    /// 服务器URL
    /// </summary>
    public string? ServerUrl => _webServerService.ServerUrl;

    /// <summary>
    /// 保存命令
    /// </summary>
    public DelegateCommand SaveCommand { get; }

    /// <summary>
    /// 测试连接命令
    /// </summary>
    public DelegateCommand TestConnectionCommand { get; }

    /// <summary>
    /// 重启服务器命令
    /// </summary>
    public DelegateCommand RestartServerCommand { get; }

    /// <summary>
    /// 重置为默认值命令
    /// </summary>
    public DelegateCommand ResetToDefaultCommand { get; }

    /// <summary>
    /// 加载设置
    /// </summary>
    private void LoadSettings()
    {
        try
        {
            Settings = _settingsService.LoadSettings<WebServerSettings>();
            Log.Information("Web服务器设置已加载");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载Web服务器设置失败");
            Settings = new WebServerSettings();
        }
    }

    /// <summary>
    /// 保存设置
    /// </summary>
    private void ExecuteSave()
    {
        try
        {
            _settingsService.SaveSettings(Settings);
            Log.Information("Web服务器设置已保存");
            _notificationService.ShowSuccess("Web服务器设置已保存");
            
            // 通知属性变更
            RaisePropertyChanged(nameof(IsServerRunning));
            RaisePropertyChanged(nameof(ServerUrl));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存Web服务器设置失败");
            _notificationService.ShowError("保存Web服务器设置失败");
        }
    }

    /// <summary>
    /// 测试连接
    /// </summary>
    private async void ExecuteTestConnection()
    {
        if (IsTesting) return;

        IsTesting = true;
        TestResults = "正在测试连接...\n";

        try
        {
            var results = new List<string>();
            
            // 测试端口是否可用
            results.Add($"测试端口 {Settings.Port} 可用性...");
            var isPortAvailable = await TestPortAvailabilityAsync(Settings.Port);
            results.Add($"端口 {Settings.Port} {(isPortAvailable ? "可用" : "已被占用")}");

            // 如果服务器正在运行，测试接口响应
            if (IsServerRunning)
            {
                results.Add("\n测试API接口...");
                var isApiResponding = await TestApiResponseAsync();
                results.Add($"API接口 {(isApiResponding ? "响应正常" : "无响应或错误")}");
            }
            else
            {
                results.Add("\nWeb服务器未运行，无法测试API接口");
            }

            // 网络连通性测试
            results.Add("\n测试网络连通性...");
            var networkResults = await TestNetworkConnectivityAsync();
            results.AddRange(networkResults);

            TestResults = string.Join("\n", results);
            Log.Information("Web服务器连接测试完成");
        }
        catch (Exception ex)
        {
            TestResults += $"\n测试过程中发生错误: {ex.Message}";
            Log.Error(ex, "Web服务器连接测试失败");
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>
    /// 重启服务器
    /// </summary>
    private async void ExecuteRestartServer()
    {
        if (IsRestarting) return;

        IsRestarting = true;
        try
        {
            _notificationService.ShowSuccess("正在重启Web服务器...");
            
            // 停止服务器
            if (IsServerRunning)
            {
                await _webServerService.StopAsync();
                await Task.Delay(1000); // 等待1秒确保完全停止
            }

            // 重新加载设置并启动服务器
            Settings = _settingsService.LoadSettings<WebServerSettings>();
            await _webServerService.StartAsync();
            
            _notificationService.ShowSuccess("Web服务器重启成功");
            Log.Information("Web服务器重启成功");
            
            // 通知属性变更
            RaisePropertyChanged(nameof(IsServerRunning));
            RaisePropertyChanged(nameof(ServerUrl));
        }
        catch (Exception ex)
        {
            _notificationService.ShowError("Web服务器重启失败");
            Log.Error(ex, "Web服务器重启失败");
        }
        finally
        {
            IsRestarting = false;
        }
    }

    /// <summary>
    /// 重置为默认值
    /// </summary>
    private void ExecuteResetToDefault()
    {
        Settings = new WebServerSettings();
        _notificationService.ShowSuccess("已重置为默认设置");
        Log.Information("Web服务器设置已重置为默认值");
    }

    /// <summary>
    /// 测试端口可用性
    /// </summary>
    private async Task<bool> TestPortAvailabilityAsync(int port)
    {
        try
        {
            using var tcpClient = new System.Net.Sockets.TcpClient();
            var connectTask = tcpClient.ConnectAsync("127.0.0.1", port);
            var timeoutTask = Task.Delay(1000);
            
            var completedTask = await Task.WhenAny(connectTask, timeoutTask);
            
            if (completedTask == connectTask && tcpClient.Connected)
            {
                // 端口已被占用
                return false;
            }
            else
            {
                // 端口可用或连接超时（可能可用）
                return true;
            }
        }
        catch
        {
            // 连接失败，端口可用
            return true;
        }
    }

    /// <summary>
    /// 测试API响应
    /// </summary>
    private async Task<bool> TestApiResponseAsync()
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(5);
            
            var testUrl = $"{Settings.ServerUrl}/health"; // 可以添加健康检查端点
            var response = await httpClient.GetAsync(testUrl);
            
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 测试网络连通性
    /// </summary>
    private async Task<List<string>> TestNetworkConnectivityAsync()
    {
        var results = new List<string>();
        
        try
        {
            var ping = new Ping();
            var reply = await ping.SendPingAsync("127.0.0.1", 1000);
            
            if (reply.Status == IPStatus.Success)
            {
                results.Add($"本地回环测试: 成功 ({reply.RoundtripTime}ms)");
            }
            else
            {
                results.Add($"本地回环测试: 失败 ({reply.Status})");
            }
        }
        catch (Exception ex)
        {
            results.Add($"网络测试失败: {ex.Message}");
        }
        
        return results;
    }
} 
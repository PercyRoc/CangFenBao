using System.Net.Http;
using System.Windows.Input;
using ChileSowing.Models.Settings;
using ChileSowing.Services;
using Common.Services.Settings;
using Common.Services.Ui;
using Serilog;
using System.Net.NetworkInformation;

namespace ChileSowing.ViewModels.Settings;

public class KuaiShouSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private readonly IKuaiShouApiService _kuaiShouApiService;
    private readonly INotificationService _notificationService;
    private KuaiShouSettings _settings;
    private bool _isTesting;
    private bool _isDiagnosing;
    private string _diagnosticResults = "";

    public KuaiShouSettingsViewModel(
        ISettingsService settingsService, 
        IKuaiShouApiService kuaiShouApiService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _kuaiShouApiService = kuaiShouApiService;
        _notificationService = notificationService;
        
        _settings = _settingsService.LoadSettings<KuaiShouSettings>();
        
        // 订阅配置的属性更改事件，实现自动保存
        _settings.PropertyChanged += OnSettingsPropertyChanged;
        
        TestConnectionCommand = new DelegateCommand(async () => await ExecuteTestConnection(), () => !_isTesting);
        NetworkDiagnosticCommand = new DelegateCommand(async () => await ExecuteNetworkDiagnostic(), () => !_isDiagnosing);
        ResetToDefaultCommand = new DelegateCommand(ExecuteResetToDefault);
    }

    public KuaiShouSettings Settings
    {
        get => _settings;
        set 
        { 
            if (_settings != null)
            {
                _settings.PropertyChanged -= OnSettingsPropertyChanged;
            }
            
            SetProperty(ref _settings, value);
            
            if (_settings != null)
            {
                _settings.PropertyChanged += OnSettingsPropertyChanged;
            }
        }
    }

    public bool IsTesting
    {
        get => _isTesting;
        set
        {
            SetProperty(ref _isTesting, value);
            TestConnectionCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsDiagnosing
    {
        get => _isDiagnosing;
        set
        {
            SetProperty(ref _isDiagnosing, value);
            NetworkDiagnosticCommand.RaiseCanExecuteChanged();
        }
    }

    public string DiagnosticResults
    {
        get => _diagnosticResults;
        set => SetProperty(ref _diagnosticResults, value);
    }

    public DelegateCommand TestConnectionCommand { get; }
    public DelegateCommand NetworkDiagnosticCommand { get; }
    public ICommand ResetToDefaultCommand { get; }

    private void OnSettingsPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 配置属性更改时自动保存
        SaveSettings();
    }

    private async Task ExecuteTestConnection()
    {
        if (IsTesting) return;

        IsTesting = true;
        try
        {
            _notificationService.ShowWarning("Testing KuaiShou API connection...");
            var isConnected = await _kuaiShouApiService.TestConnectionAsync();
            
            if (isConnected)
            {
                _notificationService.ShowSuccess("KuaiShou API connection test successful");
                Log.Information("KuaiShou API connection test successful");
            }
            else
            {
                _notificationService.ShowError("KuaiShou API connection test failed. Try Network Diagnostic for detailed information.");
                Log.Warning("KuaiShou API connection test failed");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during KuaiShou API connection test");
            _notificationService.ShowError($"Connection test error: {ex.Message}");
        }
        finally
        {
            IsTesting = false;
        }
    }

    private async Task ExecuteNetworkDiagnostic()
    {
        if (IsDiagnosing) return;

        IsDiagnosing = true;
        var results = new System.Text.StringBuilder();
        
        try
        {
            _notificationService.ShowWarning("Running network diagnostic...");
            results.AppendLine("=== Network Diagnostic Results ===");
            results.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            results.AppendLine($"Target URL: {Settings.ApiUrl}");
            results.AppendLine();

            // 1. 解析URL
            Uri uri;
            try
            {
                uri = new Uri(Settings.ApiUrl);
                results.AppendLine($"✓ URL parsing successful");
                results.AppendLine($"  Host: {uri.Host}");
                results.AppendLine($"  Port: {uri.Port}");
                results.AppendLine($"  Scheme: {uri.Scheme}");
            }
            catch (Exception ex)
            {
                results.AppendLine($"✗ URL parsing failed: {ex.Message}");
                DiagnosticResults = results.ToString();
                return;
            }

            // 2. Ping测试
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(uri.Host, 3000);
                if (reply.Status == IPStatus.Success)
                {
                    results.AppendLine($"✓ Ping test successful");
                    results.AppendLine($"  Round trip time: {reply.RoundtripTime}ms");
                }
                else
                {
                    results.AppendLine($"✗ Ping test failed: {reply.Status}");
                }
            }
            catch (Exception ex)
            {
                results.AppendLine($"✗ Ping test error: {ex.Message}");
            }

            // 3. TCP连接测试
            try
            {
                using var tcpClient = new System.Net.Sockets.TcpClient();
                using var connectCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(5000));
                await tcpClient.ConnectAsync(uri.Host, uri.Port, connectCts.Token);
                results.AppendLine($"✓ TCP connection successful");
                results.AppendLine($"  Connected to {uri.Host}:{uri.Port}");
            }
            catch (Exception ex)
            {
                results.AppendLine($"✗ TCP connection failed: {ex.Message}");
            }

            // 4. HTTP连接测试
            try
            {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromMilliseconds(10000);
                
                var response = await httpClient.GetAsync($"{uri.Scheme}://{uri.Host}:{uri.Port}/", HttpCompletionOption.ResponseHeadersRead);
                results.AppendLine($"✓ HTTP connection successful");
                results.AppendLine($"  Status: {response.StatusCode}");
                results.AppendLine($"  Server: {response.Headers.Server?.ToString() ?? "Unknown"}");
            }
            catch (Exception ex)
            {
                results.AppendLine($"✗ HTTP connection failed: {ex.Message}");
            }

            // 5. API端点测试
            try
            {
                var isConnected = await _kuaiShouApiService.TestConnectionAsync();
                if (isConnected)
                {
                    results.AppendLine($"✓ KuaiShou API endpoint test successful");
                }
                else
                {
                    results.AppendLine($"✗ KuaiShou API endpoint test failed");
                }
            }
            catch (Exception ex)
            {
                results.AppendLine($"✗ KuaiShou API endpoint test error: {ex.Message}");
            }

            results.AppendLine();
            results.AppendLine("=== Diagnostic Completed ===");
            
            _notificationService.ShowSuccess("Network diagnostic completed. Check results below.");
        }
        catch (Exception ex)
        {
            results.AppendLine($"✗ Diagnostic error: {ex.Message}");
            Log.Error(ex, "Error during network diagnostic");
            _notificationService.ShowError($"Diagnostic error: {ex.Message}");
        }
        finally
        {
            DiagnosticResults = results.ToString();
            IsDiagnosing = false;
        }
    }

    private void ExecuteResetToDefault()
    {
        Settings = new KuaiShouSettings();
        _notificationService.ShowWarning("Settings reset to default values");
    }

    public void SaveSettings()
    {
        try
        {
            _settingsService.SaveSettings(Settings);
            Log.Information("KuaiShou settings saved successfully");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save KuaiShou settings");
            throw;
        }
    }
} 
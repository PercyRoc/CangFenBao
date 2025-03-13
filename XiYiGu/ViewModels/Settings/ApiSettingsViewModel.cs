using Presentation_XiYiGu.Models.Settings;
using Presentation_XiYiGu.Services;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using Common.Services.Settings;
using Common.Services.Ui;

namespace Presentation_XiYiGu.ViewModels.Settings;

/// <summary>
/// API设置视图模型
/// </summary>
public class ApiSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private readonly WaybillUploadService _waybillUploadService;
    private readonly INotificationService _notificationService;
    private ApiSettings _apiSettings = new();
    private bool _isLoading;
    private int _testCount = 50;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="settingsService">设置服务</param>
    /// <param name="waybillUploadService">运单上传服务</param>
    /// <param name="notificationService">通知服务</param>
    public ApiSettingsViewModel(
        ISettingsService settingsService,
        WaybillUploadService waybillUploadService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _waybillUploadService = waybillUploadService;
        _notificationService = notificationService;
        
        TestUploadCommand = new DelegateCommand(ExecuteTestUpload, CanTestUpload)
            .ObservesProperty(() => ApiSettings.Enabled)
            .ObservesProperty(() => IsLoading);
            
        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);
            
        LoadSettings();
    }

    /// <summary>
    /// API设置
    /// </summary>
    public ApiSettings ApiSettings
    {
        get => _apiSettings;
        set => SetProperty(ref _apiSettings, value);
    }
    
    /// <summary>
    /// 是否正在加载
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }
    
    /// <summary>
    /// 测试数量
    /// </summary>
    public int TestCount
    {
        get => _testCount;
        set => SetProperty(ref _testCount, value);
    }
    
    /// <summary>
    /// 测试上传命令
    /// </summary>
    public DelegateCommand TestUploadCommand { get; }
    
    /// <summary>
    /// 保存配置命令
    /// </summary>
    public DelegateCommand SaveConfigurationCommand { get; }
    
    /// <summary>
    /// 加载设置
    /// </summary>
    private void LoadSettings()
    {
        try
        {
            ApiSettings = _settingsService.LoadSettings<ApiSettings>();
            Log.Information("API设置已加载");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载API设置时发生错误");
        }
    }
    
    /// <summary>
    /// 执行测试上传
    /// </summary>
    private async void ExecuteTestUpload()
    {
        if (IsLoading) return;
        
        try
        {
            IsLoading = true;
            
            // 重新加载最新的 API 设置
            LoadSettings();
            
            // 执行批量测试
            var results = await _waybillUploadService.TestBatchUploadAsync(TestCount);
            
            // 统计结果
            var successCount = results.Count(r => r.InfoResponse.IsSuccess);
            var imageSuccessCount = results.Count(r => r.ImageResponse?.IsSuccess == true);
            
            // 显示结果
            _notificationService.ShowSuccess(
                $"批量测试完成：共{TestCount}条，运单信息成功{successCount}条，图片上传成功{imageSuccessCount}条");
            
            // 如果有失败的记录，显示详细信息
            var failedRecords = results.Where(r => !r.InfoResponse.IsSuccess || (r.ImageResponse != null && !r.ImageResponse.IsSuccess));
            foreach (var record in failedRecords)
            {
                var errorMsg = $"运单号：{record.Barcode}\n";
                if (!record.InfoResponse.IsSuccess)
                {
                    errorMsg += $"运单信息上传失败：{record.InfoResponse.Msg}\n";
                }
                if (record.ImageResponse != null && !record.ImageResponse.IsSuccess)
                {
                    errorMsg += $"图片上传失败：{record.ImageResponse.Msg}";
                }
                _notificationService.ShowWarning(errorMsg);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "批量测试时发生错误");
            _notificationService.ShowError($"批量测试异常: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }
    
    /// <summary>
    /// 是否可以测试上传
    /// </summary>
    private bool CanTestUpload()
    {
        return ApiSettings.Enabled && !IsLoading;
    }

    /// <summary>
    /// 执行保存配置
    /// </summary>
    private void ExecuteSaveConfiguration()
    {
        try
        {
            _settingsService.SaveSettings(ApiSettings);
            Log.Information("API设置已保存");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存API设置时发生错误");
        }
    }
} 
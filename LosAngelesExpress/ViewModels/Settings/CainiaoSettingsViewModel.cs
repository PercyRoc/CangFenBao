using Common.Models.Package;
using Common.Services.Settings;
using LosAngelesExpress.Models.Settings;
using LosAngelesExpress.Services;
using Serilog;
using System.ComponentModel;
using System.Text.Json;
using Common.Services.Notifications;

namespace LosAngelesExpress.ViewModels.Settings;

/// <summary>
/// 菜鸟API设置 ViewModel
/// </summary>
public class CainiaoSettingsViewModel : BindableBase, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly ICainiaoApiService _cainiaoApiService;
    private readonly INotificationService _notificationService;
    private string _testBarcode = null!;

    public DelegateCommand TestApiCommand { get; }

    public CainiaoSettingsViewModel(
        ISettingsService settingsService,
        ICainiaoApiService cainiaoApiService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _cainiaoApiService = cainiaoApiService;
        _notificationService = notificationService;

        // 从设置服务加载原始设置
        var originalSettings = _settingsService.LoadSettings<CainiaoApiSettings>();
        // 克隆一个副本用于UI绑定和编辑，避免直接修改缓存中的实例
        Settings = JsonSerializer.Deserialize<CainiaoApiSettings>(JsonSerializer.Serialize(originalSettings)) ?? new CainiaoApiSettings();
        
        TestApiCommand = new DelegateCommand(async () => await ExecuteTestApiCommandAsync());
    }

    /// <summary>
    /// 菜鸟API设置
    /// </summary>
    public CainiaoApiSettings Settings { get; }

    /// <summary>
    /// 测试条码
    /// </summary>
    public string TestBarcode
    {
        get => _testBarcode;
        set => SetProperty(ref _testBarcode, value);
    }

    /// <summary>
    /// 保存设置（由外部调用）
    /// </summary>
    public void SaveSettings()
    {
        try
        {
            _settingsService.SaveSettings(Settings);
            Log.Information("菜鸟API设置已保存");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存菜鸟API设置时发生错误");
            throw;
        }
    }

    /// <summary>
    /// 执行API测试命令
    /// </summary>
    private async Task ExecuteTestApiCommandAsync()
    {
        if (string.IsNullOrWhiteSpace(TestBarcode))
        {
            _notificationService.ShowWarning("请输入条码进行测试");
            return;
        }

        // 使用 PackageInfo.Create() 工厂方法创建实例
        var dummyPackageInfo = PackageInfo.Create();
        dummyPackageInfo.SetBarcode(TestBarcode);
        dummyPackageInfo.Weight = 2.129; // 对应 2129g
        dummyPackageInfo.SetDimensions(36.1, 26.6, 23.0); // 对应 361mm x 266mm x 230mm

        Log.Information($"开始测试菜鸟API，条码: {TestBarcode}");
        _notificationService.ShowSuccess($"正在测试API，条码: {TestBarcode}...");

        try
        {
            var result = await _cainiaoApiService.UploadPackageAsync(dummyPackageInfo);

            if (result.IsSuccess)
            {
                Log.Information($"菜鸟API测试成功，条码: {TestBarcode}, 响应时间: {result.ResponseTimeMs} ms, 分拣代码: {result.SortCode}");
                _notificationService.ShowSuccess($"API测试成功！条码: {TestBarcode}, 分拣代码: {result.SortCode}, 响应时间: {result.ResponseTimeMs} ms");
            }
            else
            {
                Log.Warning($"菜鸟API测试失败，条码: {TestBarcode}, 错误: {result.ErrorMessage}, HTTP状态码: {result.HttpStatusCode}, 响应时间: {result.ResponseTimeMs} ms");
                _notificationService.ShowError($"API测试失败！条码: {TestBarcode}, 错误: {result.ErrorMessage}, 响应时间: {result.ResponseTimeMs} ms");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"菜鸟API测试发生异常，条码: {TestBarcode}");
            _notificationService.ShowError($"API测试发生异常！条码: {TestBarcode}, 异常: {ex.Message}");
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
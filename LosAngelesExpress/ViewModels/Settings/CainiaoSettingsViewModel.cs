 using Common.Models.Package;
using Common.Services.Settings;
using Common.Services.Ui;
using LosAngelesExpress.Models.Settings;
using LosAngelesExpress.Services;
using Serilog;

namespace LosAngelesExpress.ViewModels.Settings;

/// <summary>
/// 菜鸟API设置 ViewModel
/// </summary>
public class CainiaoSettingsViewModel : BindableBase
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

        Settings = _settingsService.LoadSettings<CainiaoApiSettings>();

        TestApiCommand = new DelegateCommand(async void () => await ExecuteTestApiCommandAsync());
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
        dummyPackageInfo.Weight = 1.0;
        dummyPackageInfo.SetDimensions(10.11, 10.1, 10.0);

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
}
using Common.Services.Settings;
using Common.Services.Ui;
using Serilog;
using ZtCloudWarehous.Services;
using ZtCloudWarehous.Models;

namespace ZtCloudWarehous.ViewModels.Settings;

internal class WeighingSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private readonly IWeighingService _weighingService;

    public WeighingSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService,
        IWeighingService weighingService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        _weighingService = weighingService;
        
        // 加载配置
        try
        {
            Settings = _settingsService.LoadSettings<WeighingSettings>();
            Log.Information("称重设置加载成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载称重设置时发生错误");
            _notificationService.ShowError("加载称重设置时发生错误");
            Settings = new WeighingSettings();
        }

        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);
        TestNewWeighingApiCommand = new DelegateCommand(ExecuteTestNewWeighingApi, CanExecuteTestNewWeighingApi);
        
        // 监听UseNewWeighingApi属性变化，更新测试命令的可执行状态
        Settings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Settings.UseNewWeighingApi))
            {
                TestNewWeighingApiCommand.RaiseCanExecuteChanged();
            }
        };
    }

    public WeighingSettings Settings { get; }

    public DelegateCommand SaveConfigurationCommand { get; }
    public DelegateCommand TestNewWeighingApiCommand { get; }

    private void ExecuteSaveConfiguration()
    {
        try
        {
            var results = _settingsService.SaveSettings(Settings, true);
            if (results.Length > 0)
            {
                var errorMessage = string.Join("\n", results.Select(static r => r.ErrorMessage));
                _notificationService.ShowError($"保存设置失败：\n{errorMessage}");
                return;
            }

            _notificationService.ShowSuccess("称重设置已保存");
            Log.Information("称重设置已保存");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存称重设置时发生错误");
            _notificationService.ShowError("保存称重设置时发生错误");
        }
    }

    private bool CanExecuteTestNewWeighingApi()
    {
        return Settings.UseNewWeighingApi;
    }

    private async void ExecuteTestNewWeighingApi()
    {
        try
        {
            _notificationService.ShowSuccess("正在测试新称重接口...");
            
            var testRequest = new NewWeighingRequest
            {
                WaybillCode = "TEST" + DateTime.Now.ToString("yyyyMMddHHmmss"),
                Weight = "1.23"
            };

            var response = await _weighingService.SendNewWeightDataAsync(testRequest);
            
            if (response.IsSuccess)
            {
                _notificationService.ShowSuccess($"新称重接口测试成功！\n承运商代码: {response.Data?.CarrierCode}\n省份名称: {response.Data?.ProvinceName}");
                Log.Information("新称重接口测试成功: WaybillCode={WaybillCode}, CarrierCode={CarrierCode}, ProvinceName={ProvinceName}", 
                    testRequest.WaybillCode, response.Data?.CarrierCode, response.Data?.ProvinceName);
            }
            else
            {
                _notificationService.ShowWarning($"新称重接口测试失败！\n错误代码: {response.Code}\n错误消息: {response.Msg}");
                Log.Warning("新称重接口测试失败: WaybillCode={WaybillCode}, Code={Code}, Message={Message}", 
                    testRequest.WaybillCode, response.Code, response.Msg);
            }
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"测试新称重接口时发生错误: {ex.Message}");
            Log.Error(ex, "测试新称重接口时发生错误");
        }
    }
}
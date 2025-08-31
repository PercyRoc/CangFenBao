using System.Collections.ObjectModel;
using Common.Services.Settings;
using Common.Services.Ui;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using ShanghaiModuleBelt.Models;

namespace ShanghaiModuleBelt.ViewModels.Settings;

public class ModuleConfigViewModel : BindableBase
{
    private readonly ModuleConfig _config = null!;
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;

    public ModuleConfigViewModel(
        INotificationService notificationService,
        ISettingsService settingsService)
    {
        _notificationService = notificationService;
        _settingsService = settingsService;

        // 从配置文件加载配置
        Config = _settingsService.LoadSettings<ModuleConfig>();

        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);
    }

    public ModuleConfig Config
    {
        get => _config;
        private init => SetProperty(ref _config, value);
    }

    public ObservableCollection<SiteOption> SiteOptions { get; } =
    [
        new()
        {
            Value = "1001",
            Display = "1001-上海"
        },
        new()
        {
            Value = "1001-2",
            Display = "1001-2上海"
        },
        new()
        {
            Value = "1002",
            Display = "1002-深圳"
        }
    ];

    internal DelegateCommand SaveConfigurationCommand { get; }

    private void ExecuteSaveConfiguration()
    {
        try
        {
            // 保存配置到文件
            _settingsService.SaveSettings(Config);

            Log.Information("模组配置已保存");
            _notificationService.ShowSuccess("模组配置已保存");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存模组配置时发生错误");
            _notificationService.ShowError("保存失败");
        }
    }
}

public class SiteOption
{
    public string Value { get; set; } = string.Empty;
    public string Display { get; set; } = string.Empty;
}
using Camera.Models.Settings;
using Common.Services.Settings;
using Serilog;
using System.Windows.Input;

namespace Camera.ViewModels;

public class CameraSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private CameraOverallSettings _cameraSettings;

    public CameraSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadSettings();

        SaveSettingsCommand = new DelegateCommand(ExecuteSaveSettings)
            .ObservesProperty(() => CameraSettings);

        AddFilterGroupCommand = new DelegateCommand(ExecuteAddFilterGroup);
        RemoveFilterGroupCommand = new DelegateCommand<BarcodeFilterGroup>(ExecuteRemoveFilterGroup);
    }

    public CameraOverallSettings CameraSettings
    {
        get => _cameraSettings;
        private set => SetProperty(ref _cameraSettings, value);
    }

    public ICommand SaveSettingsCommand { get; }
    public ICommand AddFilterGroupCommand { get; }
    public ICommand RemoveFilterGroupCommand { get; }

    private void LoadSettings()
    {
        try
        {
            CameraSettings = _settingsService.LoadSettings<CameraOverallSettings>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载相机设置时出错。使用默认设置。");
            CameraSettings = new CameraOverallSettings();
        }
    }

    private void ExecuteSaveSettings()
    {
        try
        {
            _settingsService.SaveSettings(CameraSettings, validate: true, throwOnError: true);

            Log.Information("相机设置已成功保存。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存错误的相机设置。");
        }
    }

    private void ExecuteAddFilterGroup()
    {
        var newGroup = new BarcodeFilterGroup
            { GroupName = $"Rule Group {CameraSettings.BarcodeFilter.RuleGroups.Count + 1}" };
        CameraSettings.BarcodeFilter.RuleGroups.Add(newGroup);
        Log.Information("添加了新的条形码过滤器组：{groupName}", newGroup.GroupName);
    }

    private void ExecuteRemoveFilterGroup(BarcodeFilterGroup? groupToRemove)
    {
        if (groupToRemove == null || CameraSettings.BarcodeFilter.RuleGroups == null) return;
        if (CameraSettings.BarcodeFilter.RuleGroups.Remove(groupToRemove))
        {
            Log.Information("删除条形码过滤器组：{groupName}", groupToRemove.GroupName);
        }
    }
}
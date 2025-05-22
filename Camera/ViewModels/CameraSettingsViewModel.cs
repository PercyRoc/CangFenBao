using Camera.Models.Settings;
using Common.Services.Settings;
using Serilog;
using System.Windows.Input;
using System.Windows.Forms;
using System;
using DialogResult = System.Windows.Forms.DialogResult;

namespace Camera.ViewModels;

public class CameraSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private CameraOverallSettings _cameraSettings = null!;

    public CameraSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadSettings();

        SaveSettingsCommand = new DelegateCommand(ExecuteSaveSettings)
            .ObservesProperty(() => CameraSettings);

        AddFilterGroupCommand = new DelegateCommand(ExecuteAddFilterGroup);
        RemoveFilterGroupCommand = new DelegateCommand<BarcodeFilterGroup>(ExecuteRemoveFilterGroup);

        SelectImageSaveFolderPathCommand = new DelegateCommand(ExecuteSelectImageSaveFolderPath);
        SelectVolumeCameraImageSavePathCommand = new DelegateCommand(ExecuteSelectVolumeCameraImageSavePath);
    }

    public CameraOverallSettings CameraSettings
    {
        get => _cameraSettings;
        private set => SetProperty(ref _cameraSettings, value);
    }

    public ICommand SaveSettingsCommand { get; }
    public ICommand AddFilterGroupCommand { get; }
    public ICommand RemoveFilterGroupCommand { get; }
    public ICommand SelectImageSaveFolderPathCommand { get; }
    public ICommand SelectVolumeCameraImageSavePathCommand { get; }

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
            (SaveSettingsCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        }
    }

    private void ExecuteSelectImageSaveFolderPath()
    {
        using var dialog = new FolderBrowserDialog();
        dialog.Description = "选择图像保存文件夹";
        dialog.UseDescriptionForTitle = true;
        dialog.SelectedPath = !string.IsNullOrWhiteSpace(CameraSettings.ImageSave.SaveFolderPath) && System.IO.Directory.Exists(CameraSettings.ImageSave.SaveFolderPath) 
            ? CameraSettings.ImageSave.SaveFolderPath 
            : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        if (dialog.ShowDialog() != DialogResult.OK) return;
        CameraSettings.ImageSave.SaveFolderPath = dialog.SelectedPath;
        (SaveSettingsCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        Log.Information("图像保存文件夹已更新为: {path}", dialog.SelectedPath);
    }

    private void ExecuteSelectVolumeCameraImageSavePath()
    {
        using var dialog = new FolderBrowserDialog();
        dialog.Description = "选择体积相机图像保存路径";
        dialog.UseDescriptionForTitle = true;
        dialog.SelectedPath = !string.IsNullOrWhiteSpace(CameraSettings.VolumeCamera.ImageSavePath) && System.IO.Directory.Exists(CameraSettings.VolumeCamera.ImageSavePath)
            ? CameraSettings.VolumeCamera.ImageSavePath
            : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        if (dialog.ShowDialog() != DialogResult.OK) return;
        CameraSettings.VolumeCamera.ImageSavePath = dialog.SelectedPath;
        (SaveSettingsCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        Log.Information("体积相机图像保存路径已更新为: {path}", dialog.SelectedPath);
    }
}
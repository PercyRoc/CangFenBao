using Common.Services.Ui;
using KuaiLv.ViewModels.Settings;
using Serilog;
using SharedUI.ViewModels.Settings;

namespace KuaiLv.ViewModels.Dialogs;

public class SettingsDialogViewModel : BindableBase, IDialogAware, IDisposable
{
    // 保存各个设置页面的ViewModel实例
    private readonly CameraSettingsViewModel _cameraSettingsViewModel;
    private readonly INotificationService _notificationService;
    private readonly UploadSettingsViewModel _uploadSettingsViewModel;
    private readonly WarningLightSettingsViewModel _warningLightSettingsViewModel;

    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;
        RequestClose = new DialogCloseListener();

        // 创建各个设置页面的ViewModel实例
        _cameraSettingsViewModel = containerProvider.Resolve<CameraSettingsViewModel>();
        _uploadSettingsViewModel = containerProvider.Resolve<UploadSettingsViewModel>();
        _warningLightSettingsViewModel = containerProvider.Resolve<WarningLightSettingsViewModel>();

        SaveCommand = new DelegateCommand(ExecuteSave);
        CancelCommand = new DelegateCommand(ExecuteCancel);
    }

    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }

    public string Title
    {
        get => "系统设置";
    }

    public DialogCloseListener RequestClose { get; }

    public bool CanCloseDialog()
    {
        return true;
    }

    public void OnDialogClosed()
    {
        Dispose();
    }

    public void OnDialogOpened(IDialogParameters parameters)
    {
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private void ExecuteSave()
    {
        try
        {
            // 保存所有设置
            _cameraSettingsViewModel.SaveConfigurationCommand.Execute();
            _uploadSettingsViewModel.SaveConfigurationCommand.Execute();
            _warningLightSettingsViewModel.SaveConfiguration();

            Log.Information("所有设置已保存");
            _notificationService.ShowSuccess("设置已保存");
            RequestClose.Invoke(new DialogResult(ButtonResult.OK));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存设置时发生错误");
            _notificationService.ShowError("保存设置时发生错误");
        }
    }

    private void ExecuteCancel()
    {
        RequestClose.Invoke(new DialogResult(ButtonResult.Cancel));
    }
}
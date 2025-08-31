using Common.Services.Ui;
using Prism.Commands;
using Prism.Dialogs;
using Prism.Ioc;
using Prism.Mvvm;
using Rookie.ViewModels.Settings;
using Serilog;
using SharedUI.ViewModels.Settings;

namespace Rookie.ViewModels.Windows.Dialogs;

public class SettingsDialogViewModel : BindableBase, IDialogAware
{
    private readonly CameraSettingsViewModel _cameraSettingsViewModel;
    private readonly INotificationService _notificationService;
    private readonly RookieApiSettingsViewModel _rookieApiSettingsViewModel;

    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;

        // 创建各个设置页面的ViewModel实例
        _cameraSettingsViewModel = containerProvider.Resolve<CameraSettingsViewModel>();
        _rookieApiSettingsViewModel = containerProvider.Resolve<RookieApiSettingsViewModel>();

        SaveCommand = new DelegateCommand(ExecuteSave);
        CancelCommand = new DelegateCommand(ExecuteCancel);
    }

    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }

    public DialogCloseListener RequestClose { get; } = default!;

    public bool CanCloseDialog()
    {
        return true;
    }

    public void OnDialogClosed()
    {
    }

    public void OnDialogOpened(IDialogParameters parameters)
    {
    }

    private void ExecuteSave()
    {
        try
        {
            _cameraSettingsViewModel.SaveConfigurationCommand.Execute();
            _rookieApiSettingsViewModel.SaveConfiguration();

            Log.Information("所有设置已保存");
            _notificationService.ShowSuccessWithToken("Settings saved successfully", "SettingWindowGrowl");
            RequestClose.Invoke(new DialogResult(ButtonResult.OK));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存设置时发生错误");
            _notificationService.ShowErrorWithToken($"Error saving settings: {ex.Message}", "SettingWindowGrowl");
        }
    }

    private void ExecuteCancel()
    {
        RequestClose.Invoke(new DialogResult(ButtonResult.Cancel));
    }
}
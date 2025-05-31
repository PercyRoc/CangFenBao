using Camera.ViewModels;
using Common.Services.Ui;
using Serilog;
using Sunnen.ViewModels.Settings;
using Weight.ViewModels.Settings;

namespace Sunnen.ViewModels.Dialogs;

public class SettingsDialogViewModel : BindableBase, IDialogAware
{
    // 保存各个设置页面的ViewModel实例
    private readonly CameraSettingsViewModel _cameraSettingsViewModel;
    private readonly INotificationService _notificationService;
    private readonly PalletSettingsViewModel _palletSettingsViewModel;
    private readonly SangNengSettingsViewModel _sangNengSettingsViewModel;
    private readonly WeightSettingsViewModel _weightSettingsViewModel;

    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;
        // 初始化 RequestClose 属性
        RequestClose = new DialogCloseListener();

        // 创建各个设置页面的ViewModel实例
        _cameraSettingsViewModel = containerProvider.Resolve<CameraSettingsViewModel>();
        _weightSettingsViewModel = containerProvider.Resolve<WeightSettingsViewModel>();
        _palletSettingsViewModel = containerProvider.Resolve<PalletSettingsViewModel>();
        _sangNengSettingsViewModel = containerProvider.Resolve<SangNengSettingsViewModel>();

        SaveCommand = new DelegateCommand(ExecuteSave);
        CancelCommand = new DelegateCommand(ExecuteCancel);
    }

    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }

    public string Title => "System Settings";

    // Prism 9.0+ 要求
    public DialogCloseListener RequestClose { get; }

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
            // 保存所有设置
            _cameraSettingsViewModel.SaveSettingsCommand.Execute(null);
            _weightSettingsViewModel.SaveCommand.Execute();
            _palletSettingsViewModel.SaveConfigurationCommand.Execute();
            _sangNengSettingsViewModel.SaveConfigurationCommand.Execute();

            Log.Information("All settings saved");
            _notificationService.ShowSuccess("Settings saved");
            // 更新调用方式
            RequestClose.Invoke(new DialogResult(ButtonResult.OK));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings");
            _notificationService.ShowError($"Failed to save settings: {ex.Message}");
        }
    }

    private void ExecuteCancel()
    {
        // 更新调用方式
        RequestClose.Invoke(new DialogResult(ButtonResult.Cancel));
    }
}
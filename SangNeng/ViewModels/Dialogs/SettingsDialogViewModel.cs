using Common.Services.Ui;
using Prism.Commands;
using Prism.Ioc;
using Prism.Mvvm;
using Prism.Services.Dialogs;
using Serilog;
using SharedUI.ViewModels.Settings;
using Sunnen.ViewModels.Settings;

namespace Sunnen.ViewModels.Dialogs;

public class SettingsDialogViewModel : BindableBase, IDialogAware
{
    // 保存各个设置页面的ViewModel实例
    private readonly CameraSettingsViewModel _cameraSettingsViewModel;
    private readonly INotificationService _notificationService;
    private readonly PalletSettingsViewModel _palletSettingsViewModel;
    private readonly SangNengSettingsViewModel _sangNengSettingsViewModel;
    private readonly VolumeSettingsViewModel _volumeSettingsViewModel;
    private readonly WeightSettingsViewModel _weightSettingsViewModel;

    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;

        // 创建各个设置页面的ViewModel实例
        _cameraSettingsViewModel = containerProvider.Resolve<CameraSettingsViewModel>();
        _volumeSettingsViewModel = containerProvider.Resolve<VolumeSettingsViewModel>();
        _weightSettingsViewModel = containerProvider.Resolve<WeightSettingsViewModel>();
        _palletSettingsViewModel = containerProvider.Resolve<PalletSettingsViewModel>();
        _sangNengSettingsViewModel = containerProvider.Resolve<SangNengSettingsViewModel>();

        SaveCommand = new DelegateCommand(ExecuteSave);
        CancelCommand = new DelegateCommand(ExecuteCancel);
    }

    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }

    public string Title => "System Settings";

    public event Action<IDialogResult>? RequestClose;

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
            _cameraSettingsViewModel.SaveConfigurationCommand.Execute();
            _volumeSettingsViewModel.SaveConfigurationCommand.Execute();
            _weightSettingsViewModel.SaveConfigurationCommand.Execute();
            _palletSettingsViewModel.SaveConfigurationCommand.Execute();
            _sangNengSettingsViewModel.SaveConfigurationCommand.Execute();

            Log.Information("All settings saved");
            _notificationService.ShowSuccess("Settings saved");
            RequestClose?.Invoke(new DialogResult(ButtonResult.OK));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings");
            _notificationService.ShowError($"Failed to save settings: {ex.Message}");
        }
    }

    private void ExecuteCancel()
    {
        RequestClose?.Invoke(new DialogResult(ButtonResult.Cancel));
    }
}
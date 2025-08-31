using Common.Services.Ui;
using Prism.Commands;
using Prism.Dialogs;
using Prism.Ioc;
using Prism.Mvvm;
using Serilog;
using SharedUI.ViewModels.Settings;
using XinBa.ViewModels.Settings;

namespace XinBa.ViewModels;

public class SettingsDialogViewModel : BindableBase, IDialogAware
{
    private readonly CameraSettingsViewModel _cameraSettingsViewModel;
    private readonly INotificationService _notificationService;

    private readonly VolumeSettingsViewModel _volumeSettingsViewModel;
    private readonly WeightSettingsViewModel _weightSettingsViewModel;

    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;

        _cameraSettingsViewModel = containerProvider.Resolve<CameraSettingsViewModel>();
        _weightSettingsViewModel = containerProvider.Resolve<WeightSettingsViewModel>();
        _volumeSettingsViewModel = containerProvider.Resolve<VolumeSettingsViewModel>();

        SaveCommand = new DelegateCommand(ExecuteSave);
        CancelCommand = new DelegateCommand(ExecuteCancel);
    }

    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }

    public string Title => "System Settings";

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
            // Save all settings
            _cameraSettingsViewModel.SaveConfigurationCommand.Execute();
            _weightSettingsViewModel.SaveConfigurationCommand.Execute();
            _volumeSettingsViewModel.SaveConfigurationCommand.Execute();
            Log.Information("All settings have been saved");
            _notificationService.ShowSuccess("Settings saved");
            RequestClose.Invoke(new DialogResult(ButtonResult.OK));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred while saving settings");
            _notificationService.ShowError("Error saving settings");
        }
    }

    private void ExecuteCancel()
    {
        RequestClose.Invoke(new DialogResult(ButtonResult.Cancel));
    }
}
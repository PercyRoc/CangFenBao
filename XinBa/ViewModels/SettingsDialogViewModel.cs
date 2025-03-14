using Common.Services.Ui;
using Prism.Commands;
using Prism.Ioc;
using Prism.Mvvm;
using Prism.Services.Dialogs;
using Serilog;
using XinBa.ViewModels.Settings;

namespace XinBa.ViewModels;

public class SettingsDialogViewModel : BindableBase, IDialogAware
{
    // Store instances of each settings page ViewModel
    private readonly CameraSettingsViewModel _cameraSettingsViewModel;
    private readonly INotificationService _notificationService;

    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;

        // Create instances of each settings page ViewModel
        _cameraSettingsViewModel = containerProvider.Resolve<CameraSettingsViewModel>();

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
            // Save all settings
            _cameraSettingsViewModel.SaveConfigurationCommand.Execute(null);

            Log.Information("All settings have been saved");
            _notificationService.ShowSuccess("Settings saved");
            RequestClose?.Invoke(new DialogResult(ButtonResult.OK));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred while saving settings");
            _notificationService.ShowError("Error saving settings");
        }
    }

    private void ExecuteCancel()
    {
        RequestClose?.Invoke(new DialogResult(ButtonResult.Cancel));
    }
}
using Camera.ViewModels;
using Common.Services.Ui;
using Rookie.ViewModels.Settings;
using Serilog;
using SharedUI.ViewModels;
using SharedUI.ViewModels.Settings;
using Sorting_Car.ViewModels;
using Weight.ViewModels.Settings;

namespace Rookie.ViewModels.Windows.Dialogs;

public class SettingsDialogViewModel: BindableBase, IDialogAware
{
    private readonly CameraSettingsViewModel _cameraSettingsViewModel;
    private readonly RookieApiSettingsViewModel _rookieApiSettingsViewModel;
    private readonly CarConfigViewModel _carConfigViewModel;

    private readonly BarcodeChuteSettingsViewModel _barcodeChuteSettingsViewModel;
    private readonly WeightSettingsViewModel _weightSettingsViewModel;
    private readonly SerialPortSettingsViewModel _serialPortSettingsViewModel;

    private readonly INotificationService _notificationService;
    
    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;

        // 创建各个设置页面的ViewModel实例
        _cameraSettingsViewModel = containerProvider.Resolve<CameraSettingsViewModel>();
        _rookieApiSettingsViewModel = containerProvider.Resolve<RookieApiSettingsViewModel>();
        _carConfigViewModel = containerProvider.Resolve<CarConfigViewModel>();
        _barcodeChuteSettingsViewModel = containerProvider.Resolve<BarcodeChuteSettingsViewModel>();
        _weightSettingsViewModel = containerProvider.Resolve<WeightSettingsViewModel>();
        _serialPortSettingsViewModel = containerProvider.Resolve<SerialPortSettingsViewModel>();
        SaveCommand = new DelegateCommand(ExecuteSave);
        CancelCommand = new DelegateCommand(ExecuteCancel);
    }
    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }
    
    public DialogCloseListener RequestClose => default!;

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
            _cameraSettingsViewModel.SaveSettingsCommand.Execute(null);
            _weightSettingsViewModel.SaveCommand.Execute();
            _rookieApiSettingsViewModel.SaveConfiguration();
            _carConfigViewModel.SaveConfigCommand.Execute();
            _barcodeChuteSettingsViewModel.SaveConfigurationCommand.Execute();
            _serialPortSettingsViewModel.SaveConfigurationCommand.Execute();
            Log.Information("所有设置已保存");
            _notificationService.ShowSuccess("Settings saved successfully");
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
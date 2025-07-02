using BenFly.ViewModels.Settings;
using Common.Services.Ui;
using Serilog;
using SharedUI.ViewModels.Settings;

namespace BenFly.ViewModels.Dialogs;

internal class SettingsDialogViewModel : BindableBase, IDialogAware
{
    private readonly BalanceSortSettingsViewModel _balanceSortSettingsViewModel;

    private readonly BeltSettingsViewModel _beltSettingsViewModel;

    // 保存各个设置页面的ViewModel实例
    private readonly CameraSettingsViewModel _cameraSettingsViewModel;
    private readonly ChuteSettingsViewModel _chuteSettingsViewModel;
    private readonly INotificationService _notificationService;
    private readonly UploadSettingsViewModel _uploadSettingsViewModel;

    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;

        // 创建各个设置页面的ViewModel实例
        _cameraSettingsViewModel = containerProvider.Resolve<CameraSettingsViewModel>();
        _balanceSortSettingsViewModel = containerProvider.Resolve<BalanceSortSettingsViewModel>();
        _uploadSettingsViewModel = containerProvider.Resolve<UploadSettingsViewModel>();
        _chuteSettingsViewModel = containerProvider.Resolve<ChuteSettingsViewModel>();
        _beltSettingsViewModel = containerProvider.Resolve<BeltSettingsViewModel>();

        SaveCommand = new DelegateCommand(ExecuteSave);
        CancelCommand = new DelegateCommand(ExecuteCancel);

        // Replace the event with the Prism 9.0 property
        RequestClose = new DialogCloseListener();
    }

    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }

    public string Title
    {
        get => "系统设置";
    }

    // Replace the event with the Prism 9.0 property
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
            _cameraSettingsViewModel.SaveConfigurationCommand.Execute();
            _balanceSortSettingsViewModel.SaveConfigurationCommand.Execute();
            _uploadSettingsViewModel.SaveConfigurationCommand.Execute();
            _chuteSettingsViewModel.SaveCommand.Execute(null);
            _beltSettingsViewModel.SaveConfigurationCommand.Execute();

            Log.Information("所有设置已保存");
            _notificationService.ShowSuccess("设置已保存");
            // Update invocation to use the property's Invoke method
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
        // Update invocation to use the property's Invoke method
        RequestClose.Invoke(new DialogResult(ButtonResult.Cancel));
    }
}
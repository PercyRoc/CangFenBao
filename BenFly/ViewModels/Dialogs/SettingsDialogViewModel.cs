using Common.Services.Ui;
using Presentation_BenFly.ViewModels.Settings;
using Prism.Commands;
using Prism.Ioc;
using Prism.Mvvm;
using Prism.Services.Dialogs;
using Serilog;
using SharedUI.ViewModels.Settings;

namespace Presentation_BenFly.ViewModels.Dialogs;

public class SettingsDialogViewModel : BindableBase, IDialogAware
{
    // 保存各个设置页面的ViewModel实例
    private readonly CameraSettingsViewModel _cameraSettingsViewModel;
    private readonly ChuteSettingsViewModel _chuteSettingsViewModel;
    private readonly INotificationService _notificationService;
    private readonly BalanceSortSettingsViewModel _balanceSortSettingsViewModel;
    private readonly UploadSettingsViewModel _uploadSettingsViewModel;
    private readonly BeltSettingsViewModel _beltSettingsViewModel;

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
    }

    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }

    public string Title => "系统设置";

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
            _balanceSortSettingsViewModel.SaveConfigurationCommand.Execute();
            _uploadSettingsViewModel.SaveConfigurationCommand.Execute();
            _chuteSettingsViewModel.SaveCommand.Execute(null);
            _beltSettingsViewModel.SaveConfigurationCommand.Execute();

            Log.Information("所有设置已保存");
            _notificationService.ShowSuccess("设置已保存");
            RequestClose?.Invoke(new DialogResult(ButtonResult.OK));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存设置时发生错误");
            _notificationService.ShowError("保存设置时发生错误");
        }
    }

    private void ExecuteCancel()
    {
        RequestClose?.Invoke(new DialogResult(ButtonResult.Cancel));
    }
}
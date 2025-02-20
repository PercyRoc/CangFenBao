using Presentation_BenFly.ViewModels.Settings;
using Presentation_CommonLibrary.Services;
using Prism.Commands;
using Prism.Ioc;
using Prism.Mvvm;
using Prism.Services.Dialogs;
using Serilog;

namespace Presentation_BenFly.ViewModels.Dialogs;

public class SettingsDialogViewModel : BindableBase, IDialogAware
{
    // 保存各个设置页面的ViewModel实例
    private readonly CameraSettingsViewModel _cameraSettingsViewModel;
    private readonly INotificationService _notificationService;
    private readonly SortSettingsViewModel _sortSettingsViewModel;
    private readonly UploadSettingsViewModel _uploadSettingsViewModel;
    private readonly ChuteSettingsViewModel _chuteSettingsViewModel;

    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;

        // 创建各个设置页面的ViewModel实例
        _cameraSettingsViewModel = containerProvider.Resolve<CameraSettingsViewModel>();
        _sortSettingsViewModel = containerProvider.Resolve<SortSettingsViewModel>();
        _uploadSettingsViewModel = containerProvider.Resolve<UploadSettingsViewModel>();
        _chuteSettingsViewModel = containerProvider.Resolve<ChuteSettingsViewModel>();

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
            _sortSettingsViewModel.SaveConfigurationCommand.Execute();
            _uploadSettingsViewModel.SaveConfigurationCommand.Execute();
            _chuteSettingsViewModel.SaveCommand.Execute(null);

            Log.Information("所有设置已保存");
            _notificationService.ShowSuccess("设置已保存");
            RequestClose?.Invoke(new DialogResult(ButtonResult.OK));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存设置时发生错误");
            _notificationService.ShowError("保存设置时发生错误", ex.Message);
        }
    }

    private void ExecuteCancel()
    {
        RequestClose?.Invoke(new DialogResult(ButtonResult.Cancel));
    }
}
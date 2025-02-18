using Presentation_BenFly.ViewModels.Settings;
using Presentation_BenFly.Views.Settings;
using Presentation_CommonLibrary.Services;
using Prism.Commands;
using Prism.Ioc;
using Prism.Mvvm;
using Prism.Services.Dialogs;
using Serilog;
using Wpf.Ui.Controls;

namespace Presentation_BenFly.ViewModels.Dialogs;

public class SettingsDialogViewModel : BindableBase, IDialogAware
{
    // 保存各个设置页面的ViewModel实例
    private readonly CameraSettingsViewModel _cameraSettingsViewModel;
    private readonly INotificationService _notificationService;
    private readonly SortSettingsViewModel _sortSettingsViewModel;
    private readonly UploadSettingsViewModel _uploadSettingsViewModel;

    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;

        // 创建各个设置页面的ViewModel实例
        _cameraSettingsViewModel = containerProvider.Resolve<CameraSettingsViewModel>();
        _sortSettingsViewModel = containerProvider.Resolve<SortSettingsViewModel>();
        _uploadSettingsViewModel = containerProvider.Resolve<UploadSettingsViewModel>();

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

    public void OnNavigating(NavigatingCancelEventArgs e)
    {
        // 如果需要，可以在这里处理导航前的逻辑
        // 例如：阻止导航、保存当前页面的更改等
    }

    public void OnNavigated(NavigatedEventArgs e)
    {
        switch (e.Page)
        {
            case CameraSettingsView cameraView:
                cameraView.DataContext = _cameraSettingsViewModel;
                break;
            case SortSettingsView sortView:
                sortView.DataContext = _sortSettingsViewModel;
                break;
            case UploadSettingsView uploadView:
                uploadView.DataContext = _uploadSettingsViewModel;
                break;
        }
    }

    private void ExecuteSave()
    {
        try
        {
            // 保存所有设置
            _cameraSettingsViewModel.SaveConfigurationCommand.Execute();
            _sortSettingsViewModel.SaveConfigurationCommand.Execute();
            _uploadSettingsViewModel.SaveConfigurationCommand.Execute();

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
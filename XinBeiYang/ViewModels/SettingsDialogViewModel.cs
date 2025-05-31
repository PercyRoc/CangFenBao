using Common.Services.Ui;
using Serilog;
using XinBeiYang.ViewModels.Settings;

namespace XinBeiYang.ViewModels;

public class SettingsDialogViewModel : BindableBase, IDialogAware, IDisposable
{
    // 保存各个设置页面的ViewModel实例
    private readonly CameraSettingsViewModel _cameraSettingsViewModel;
    private readonly HostSettingsViewModel _hostSettingsViewModel;
    private readonly INotificationService _notificationService;
    private readonly WeightSettingsViewModel _chineseWeightSettingsViewModel;
    
    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;
        RequestClose = new DialogCloseListener();

        // 创建各个设置页面的ViewModel实例
        _cameraSettingsViewModel = containerProvider.Resolve<CameraSettingsViewModel>();
        _hostSettingsViewModel = containerProvider.Resolve<HostSettingsViewModel>();
        _chineseWeightSettingsViewModel = containerProvider.Resolve<WeightSettingsViewModel>();

        SaveCommand = new DelegateCommand(ExecuteSave);
        CancelCommand = new DelegateCommand(ExecuteCancel);
    }
    
    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }

    public string Title => "系统设置";

    public DialogCloseListener RequestClose { get; }

    public bool CanCloseDialog()
    {
        return true;
    }

    public void OnDialogClosed()
    {
        Dispose();
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
            _hostSettingsViewModel.SaveConfigurationCommand.Execute();
            _chineseWeightSettingsViewModel.SaveConfigurationCommand.Execute();
            _notificationService.ShowSuccess("设置已保存");
            RequestClose.Invoke(new DialogResult(ButtonResult.OK));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存设置时发生错误");
            _notificationService.ShowError($"保存设置失败: {ex.Message}");
        }
    }

    private void ExecuteCancel()
    {
        RequestClose.Invoke(new DialogResult(ButtonResult.Cancel));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
    
}
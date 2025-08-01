using Common.Services.Ui;
using FridFlipMachine.ViewModels.Settings;
using Serilog;
using SharedUI.ViewModels.Settings;

namespace FridFlipMachine.ViewModels;

internal class SettingsDialogViewModel : BindableBase, IDialogAware
{
    // 保存各个设置页面的ViewModel实例
    private readonly FridSettingsViewModel _fridSettingsViewModel;
    private readonly INotificationService _notificationService;
    private readonly PlateTurnoverSettingsViewModel _plateTurnoverSettingsViewModel;

    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;
        // 初始化 RequestClose 属性
        RequestClose = new DialogCloseListener();

        // 创建各个设置页面的ViewModel实例
        _fridSettingsViewModel = containerProvider.Resolve<FridSettingsViewModel>();
        _plateTurnoverSettingsViewModel = containerProvider.Resolve<PlateTurnoverSettingsViewModel>();

        SaveCommand = new DelegateCommand(ExecuteSave);
        CancelCommand = new DelegateCommand(ExecuteCancel);
    }

    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }

    public string Title
    {
        get => "系统设置";
    }

    // Prism 9.0+ 要求
    public DialogCloseListener RequestClose { get; }

    public bool CanCloseDialog()
    {
        return true;
    }

    public void OnDialogClosed()
    {
        // 在这里可以释放资源，例如取消订阅等
        _plateTurnoverSettingsViewModel.Dispose();
    }

    public void OnDialogOpened(IDialogParameters parameters)
    {
    }

    private void ExecuteSave()
    {
        try
        {
            // 保存所有设置
            _fridSettingsViewModel.SaveConfigurationCommand.Execute();
            _plateTurnoverSettingsViewModel.SaveConfigurationCommand.Execute();

            Log.Information("所有设置已保存");
            _notificationService.ShowSuccess("设置已保存");
            // 更新调用方式
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
        // 更新调用方式
        RequestClose.Invoke(new DialogResult(ButtonResult.Cancel));
    }
}
using Common.Services.Ui;
using Serilog;
using WeiCiModule.ViewModels.Settings;

namespace WeiCiModule.ViewModels;

public class SettingsDialogViewModel: BindableBase, IDialogAware, IDisposable
{
    private readonly INotificationService _notificationService;
    private readonly ModulesTcpSettingsViewModel  _modulesTcpSettingsViewModel;
    private readonly ChuteSettingsViewModel _chuteSettingsViewModel;

    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;
        _modulesTcpSettingsViewModel =  containerProvider.Resolve<ModulesTcpSettingsViewModel>();
        _chuteSettingsViewModel = containerProvider.Resolve<ChuteSettingsViewModel>();
        RequestClose = new DialogCloseListener();

        SaveCommand = new DelegateCommand(ExecuteSave);
        CancelCommand = new DelegateCommand(ExecuteCancel);
    }

    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }

    // Prism 9.0+ 要求
    public DialogCloseListener RequestClose { get; }

    public bool CanCloseDialog()
    {
        return true;
    }

    public void OnDialogClosed()
    {
        // 对话框关闭时释放资源
        Dispose();
    }

    public void OnDialogOpened(IDialogParameters parameters)
    {
    }

    private void ExecuteSave()
    {
        try
        {
            Log.Information("所有设置已保存");
            _notificationService.ShowSuccess("设置已保存");
            _modulesTcpSettingsViewModel.SaveConfigurationCommand.Execute();
            _chuteSettingsViewModel.SaveCommand.Execute();
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

    // 实现 IDisposable 接口
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
using Serilog;
using SowingSorting.ViewModels.Settings;
using ChileSowing.ViewModels.Settings;
using Common.Services.Notifications;

namespace ChileSowing.ViewModels;

public class SettingsDialogViewModel: BindableBase, IDialogAware, IDisposable
{
    private readonly INotificationService _notificationService;

    private readonly ModbusTcpSettingsViewModel _modbusTcpSettingsViewModel;
    
    private readonly KuaiShouSettingsViewModel _kuaiShouSettingsViewModel;
    
    private readonly WebServerSettingsViewModel _webServerSettingsViewModel;
    
    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;
        RequestClose = new DialogCloseListener();
        _modbusTcpSettingsViewModel = containerProvider.Resolve<ModbusTcpSettingsViewModel>();
        _kuaiShouSettingsViewModel = containerProvider.Resolve<KuaiShouSettingsViewModel>();
        _webServerSettingsViewModel = containerProvider.Resolve<WebServerSettingsViewModel>();
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
            _modbusTcpSettingsViewModel.SaveCommand.Execute();
            _kuaiShouSettingsViewModel.SaveSettings();
            _webServerSettingsViewModel.SaveCommand.Execute();
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

    // 实现 IDisposable 接口
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
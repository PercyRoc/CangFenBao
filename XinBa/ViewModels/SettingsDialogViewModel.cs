using Common.Services.Ui;
using Serilog;

namespace XinBa.ViewModels;

public class SettingsDialogViewModel: BindableBase, IDialogAware, IDisposable
{
    private readonly INotificationService _notificationService;

    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;
        
        // 初始化 RequestClose 属性
        RequestClose = new DialogCloseListener();

        SaveCommand = new DelegateCommand(ExecuteSave);
        CancelCommand = new DelegateCommand(ExecuteCancel);
    }

    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }

    public string Title => "System Settings";

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
            _notificationService.ShowSuccess("Settings saved successfully");
            // 更新调用方式
            RequestClose.Invoke(new DialogResult(ButtonResult.OK));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存设置时发生错误");
            _notificationService.ShowError($"Failed to save settings: {ex.Message}");
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
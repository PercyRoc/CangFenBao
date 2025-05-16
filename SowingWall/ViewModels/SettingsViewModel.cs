using Common.Services.Ui;
using Serilog;

namespace SowingWall.ViewModels;

public class SettingsViewModel :BindableBase, IDialogAware, IDisposable
{
   
    private readonly INotificationService _notificationService;
   
    
    public SettingsViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;
        RequestClose = new DialogCloseListener();

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
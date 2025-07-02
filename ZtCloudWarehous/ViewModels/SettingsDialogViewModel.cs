using Common.Services.Ui;
using Serilog;
using SharedUI.ViewModels.Settings;
using ZtCloudWarehous.ViewModels.Settings;

namespace ZtCloudWarehous.ViewModels;

internal class SettingsDialogViewModel : BindableBase, IDialogAware
{
    private readonly BarcodeChuteSettingsViewModel _barcodeChuteSettingsViewModel;

    // 保存各个设置页面的ViewModel实例
    private readonly CameraSettingsViewModel _cameraSettingsViewModel;
    private readonly INotificationService _notificationService;
    private readonly BalanceSortSettingsViewModel _sortSettingsViewModel;
    private readonly WeighingSettingsViewModel _weighingSettingsViewModel;
    private readonly XiyiguAPiSettingsViewModel _xiyiguAPiSettingsViewModel;

    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;
        // 初始化 RequestClose 属性
        RequestClose = new DialogCloseListener();

        // 创建各个设置页面的ViewModel实例
        _cameraSettingsViewModel = containerProvider.Resolve<CameraSettingsViewModel>();
        _sortSettingsViewModel = containerProvider.Resolve<BalanceSortSettingsViewModel>();
        _weighingSettingsViewModel = containerProvider.Resolve<WeighingSettingsViewModel>();
        _barcodeChuteSettingsViewModel = containerProvider.Resolve<BarcodeChuteSettingsViewModel>();
        _xiyiguAPiSettingsViewModel = containerProvider.Resolve<XiyiguAPiSettingsViewModel>();
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
            _weighingSettingsViewModel.SaveConfigurationCommand.Execute();
            _barcodeChuteSettingsViewModel.SaveConfigurationCommand.Execute();
            _xiyiguAPiSettingsViewModel.SaveConfigurationCommand.Execute();
            Log.Information("所有设置已保存");
            _notificationService.ShowSuccess("设置已保存");
            // 更新调用方式
            RequestClose.Invoke(new DialogResult(ButtonResult.OK));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存设置时发生错误");
            _notificationService.ShowError("保存设置时发生错误");
            // 考虑是否在出错时关闭
            // RequestClose.Invoke(new DialogResult(ButtonResult.Abort));
        }
    }

    private void ExecuteCancel()
    {
        // 更新调用方式
        RequestClose.Invoke(new DialogResult(ButtonResult.Cancel));
    }
}
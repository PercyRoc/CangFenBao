using Common.Services.Ui;
using FuzhouPolicyForce.ViewModels.Settings;
using Serilog;
using SharedUI.ViewModels.Settings;

namespace FuzhouPolicyForce.ViewModels;

internal class SettingsDialogViewModel : BindableBase, IDialogAware
{
    private readonly BalanceSortSettingsViewModel _balanceSortSettingsViewModel;

    private readonly BarcodeChuteSettingsViewModel _barcodeChuteSettingsViewModel;

    private readonly ShenTongLanShouSettingsViewModel _shenTongLanShouSettingsViewModel;

    // 保存各个设置页面的ViewModel实例
    private readonly CameraSettingsViewModel _cameraSettingsViewModel;
    private readonly WangDianTongSettingsViewModel _wangDianTongSettingsViewModel;
    private readonly INotificationService _notificationService;

    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;
        // 初始化 RequestClose 属性
        RequestClose = new DialogCloseListener();

        // 创建各个设置页面的ViewModel实例
        _cameraSettingsViewModel = containerProvider.Resolve<CameraSettingsViewModel>();
        _balanceSortSettingsViewModel = containerProvider.Resolve<BalanceSortSettingsViewModel>();
        _barcodeChuteSettingsViewModel = containerProvider.Resolve<BarcodeChuteSettingsViewModel>();
        _wangDianTongSettingsViewModel = containerProvider.Resolve<WangDianTongSettingsViewModel>();
        _shenTongLanShouSettingsViewModel = containerProvider.Resolve<ShenTongLanShouSettingsViewModel>();
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
            _barcodeChuteSettingsViewModel.SaveConfigurationCommand.Execute();
            _wangDianTongSettingsViewModel.SaveConfigurationCommand.Execute();
            _shenTongLanShouSettingsViewModel.SaveCommand.Execute();
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
﻿using Common.Services.Ui;
using Serilog;
using SharedUI.ViewModels;
using SharedUI.ViewModels.Settings;

namespace HuiXin.ViewModels.Dialogs;

public class SettingsDialogViewModel : BindableBase, IDialogAware
{
    private readonly BalanceSortSettingsViewModel _balanceSortSettingsViewModel;
    private readonly BarcodeChuteSettingsViewModel _barcodeChuteSettingsViewModel;

    // 保存各个设置页面的ViewModel实例
    private readonly CameraSettingsViewModel _cameraSettingsViewModel;
    private readonly CarConfigViewModel _carConfigViewModel;
    private readonly JushuitanSettingsViewModel _jushuitanSettingsViewModel;
    private readonly INotificationService _notificationService;
    private readonly SerialPortSettingsViewModel _serialPortSettingsViewModel;

    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;

        // 创建各个设置页面的ViewModel实例
        _cameraSettingsViewModel = containerProvider.Resolve<CameraSettingsViewModel>();
        _balanceSortSettingsViewModel = containerProvider.Resolve<BalanceSortSettingsViewModel>();
        _carConfigViewModel = containerProvider.Resolve<CarConfigViewModel>();
        _barcodeChuteSettingsViewModel = containerProvider.Resolve<BarcodeChuteSettingsViewModel>();
        _serialPortSettingsViewModel = containerProvider.Resolve<SerialPortSettingsViewModel>();
        _jushuitanSettingsViewModel = containerProvider.Resolve<JushuitanSettingsViewModel>();

        SaveCommand = new DelegateCommand(ExecuteSave);
        CancelCommand = new DelegateCommand(ExecuteCancel);
    }

    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }

    public string Title
    {
        get => "系统设置";
    }

    public DialogCloseListener RequestClose { get; } = default!;

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
            _cameraSettingsViewModel.SaveConfigurationCommand.Execute();
            _balanceSortSettingsViewModel.SaveConfigurationCommand.Execute();
            _carConfigViewModel.SaveConfigCommand.Execute();
            _barcodeChuteSettingsViewModel.SaveConfigurationCommand.Execute();
            _serialPortSettingsViewModel.SaveConfigurationCommand.Execute();
            _jushuitanSettingsViewModel.SaveConfigurationCommand.Execute();

            Log.Information("所有设置已保存");
            _notificationService.ShowSuccess("设置已保存");
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
        RequestClose.Invoke(new DialogResult(ButtonResult.Cancel));
    }
}
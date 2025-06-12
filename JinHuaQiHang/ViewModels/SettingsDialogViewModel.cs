using Serilog;
using Server.JuShuiTan.ViewModels;
using BalanceSorting.ViewModels.Settings;
using Common.Services.Ui;
using Common.ViewModels.Settings.ChuteRules;
using JinHuaQiHang.ViewModels.Settings;

namespace JinHuaQiHang.ViewModels;

public class SettingsDialogViewModel :BindableBase,IDialogAware
{
    private readonly JushuitanSettingsViewModel _jushuitanSettingsViewModel;
    private readonly INotificationService _notificationService;
    private readonly BalanceSortSettingsViewModel _balanceSortSettingsViewModel;
    private readonly YunDaUploadSettingsViewModel _yunDaUploadSettingsViewModel;
    private readonly ChuteRuleSettingsViewModel  _chuteRuleSettingsViewModel;

    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;
        _jushuitanSettingsViewModel = containerProvider.Resolve<JushuitanSettingsViewModel>();
        _balanceSortSettingsViewModel = containerProvider.Resolve<BalanceSortSettingsViewModel>();
        _yunDaUploadSettingsViewModel = containerProvider.Resolve<YunDaUploadSettingsViewModel>();
        _chuteRuleSettingsViewModel = containerProvider.Resolve<ChuteRuleSettingsViewModel>();
        SaveCommand = new DelegateCommand(ExecuteSave);
        CancelCommand = new DelegateCommand(ExecuteCancel);
    }
    
    public DialogCloseListener RequestClose { get; set; }
    
    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }

    public bool CanCloseDialog() => true;

    public void OnDialogClosed() { }

    public void OnDialogOpened(IDialogParameters parameters) { }
    
    private void ExecuteSave()
    {
        try
        {
            _jushuitanSettingsViewModel.SaveConfigurationCommand.Execute();
            _balanceSortSettingsViewModel.SaveConfigurationCommand.Execute();
            _yunDaUploadSettingsViewModel.SaveCommand.Execute(null);
            _chuteRuleSettingsViewModel.SaveSettingsCommand.Execute(null);
            Log.Information("所有设置已保存");
            _notificationService.ShowSuccess("Settings saved successfully");
            RequestClose.Invoke(new DialogResult(ButtonResult.OK));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存设置时发生错误");
            _notificationService.ShowErrorWithToken($"Error saving settings: {ex.Message}", "SettingWindowGrowl");
        }
    }

    private void ExecuteCancel()
    {
        RequestClose.Invoke(new DialogResult(ButtonResult.Cancel));
    }
}
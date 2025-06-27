using BalanceSorting.ViewModels.Settings;
using Common.Services.Ui;
using Serilog;
using Common.ViewModels.Settings.ChuteRules;
using XinJuLi.ViewModels.Settings;

namespace XinJuLi.ViewModels;

public class SettingsDialogViewModel : BindableBase, IDialogAware
{
    private readonly INotificationService _notificationService;
    private readonly BalanceSortSettingsViewModel _sortSettingsViewModel;
    private readonly AsnHttpSettingsViewModel _asnHttpSettingsViewModel;
    private readonly ChuteRuleSettingsViewModel _chuteRuleSettingsViewModel;
    private readonly SortingModeSettingsViewModel _sortingModeSettingsViewModel;

    public SettingsDialogViewModel(
        IContainerProvider containerProvider,
        INotificationService notificationService)
    {
        _notificationService = notificationService;
        RequestClose = new DialogCloseListener();

        _sortSettingsViewModel = containerProvider.Resolve<BalanceSortSettingsViewModel>();
        _asnHttpSettingsViewModel = containerProvider.Resolve<AsnHttpSettingsViewModel>();
        _chuteRuleSettingsViewModel = containerProvider.Resolve<ChuteRuleSettingsViewModel>();
        _sortingModeSettingsViewModel = containerProvider.Resolve<SortingModeSettingsViewModel>();
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
    }

    public void OnDialogOpened(IDialogParameters parameters)
    {
    }

    private void ExecuteSave()
    {
        try
        {
            _sortSettingsViewModel.SaveConfigurationCommand.Execute();
            _asnHttpSettingsViewModel.SaveConfigurationCommand.Execute();
            _chuteRuleSettingsViewModel.SaveSettingsCommand.Execute(null);
            _sortingModeSettingsViewModel.SaveConfiguration();
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
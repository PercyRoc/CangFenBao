using Common.Services.Ui;
using Serilog;
using ShanghaiModuleBelt.ViewModels.Settings;
using ShanghaiModuleBelt.ViewModels.Zto.Settings;
using ShanghaiModuleBelt.ViewModels.Sto.Settings;
using ShanghaiModuleBelt.ViewModels.Yunda.Settings;
using ShanghaiModuleBelt.ViewModels.Jitu.Settings;
using SharedUI.ViewModels.Settings;

namespace ShanghaiModuleBelt.ViewModels;

public class SettingsDialogViewModel : BindableBase, IDialogAware, IDisposable
{
    // 保存各个设置页面的ViewModel实例
    private readonly CameraSettingsViewModel _cameraSettingsViewModel;
    private readonly ModuleConfigViewModel _moduleConfigViewModel;
    private readonly BarcodeChuteSettingsViewModel _barcodeChuteSettingsViewModel;
    private readonly ZtoApiSettingsViewModel _ztoSettingsViewModel;
    private readonly StoApiSettingsViewModel _stoSettingsViewModel;
    private readonly YundaApiSettingsViewModel _yundaSettingsViewModel;
    private readonly JituSettingsViewModel _jituSettingsViewModel;
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
        _moduleConfigViewModel = containerProvider.Resolve<ModuleConfigViewModel>();
        _barcodeChuteSettingsViewModel = containerProvider.Resolve<BarcodeChuteSettingsViewModel>();
        _ztoSettingsViewModel = containerProvider.Resolve<ZtoApiSettingsViewModel>();
        _stoSettingsViewModel = containerProvider.Resolve<StoApiSettingsViewModel>();
        _yundaSettingsViewModel = containerProvider.Resolve<YundaApiSettingsViewModel>();
        _jituSettingsViewModel = containerProvider.Resolve<JituSettingsViewModel>();

        SaveCommand = new DelegateCommand(ExecuteSave);
        CancelCommand = new DelegateCommand(ExecuteCancel);
    }

    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }

    public string Title => "系统设置";

    // Prism 9.0+ 要求
    public DialogCloseListener RequestClose { get; }

    // 实现 IDisposable 接口
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

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
            // 保存所有设置
            _cameraSettingsViewModel.SaveConfigurationCommand.Execute();
            _moduleConfigViewModel.SaveConfigurationCommand.Execute();
            _barcodeChuteSettingsViewModel.SaveConfigurationCommand.Execute();
            // 保存快递 API 设置
            _ztoSettingsViewModel.SaveCommand.Execute();
            _stoSettingsViewModel.SaveCommand.Execute();
            _yundaSettingsViewModel.SaveSettingsCommand.Execute();
            _jituSettingsViewModel.SaveCommand.Execute();

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
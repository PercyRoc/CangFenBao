using SharedUI.ViewModels;
using SharedUI.ViewModels.Settings;
using SharedUI.Views.Dialogs;
using SharedUI.Views.Settings;
using SharedUI.Views.Windows;

namespace SharedUI.Extensions;

public static class ServiceCollectionExtensions
{
    public static void AddShardUi(this IContainerRegistry services)
    {
        services.RegisterForNavigation<CameraSettingsView, CameraSettingsViewModel>();
        services.RegisterForNavigation<BalanceSortSettingsView, BalanceSortSettingsViewModel>();
        services.RegisterForNavigation<ChineseWeightSettingsView, ChineseWeightSettingsViewModel>();
        // 注册通用的确认对话框
        services.RegisterDialog<HistoryDialogView, HistoryDialogViewModel>();
        services.RegisterDialogWindow<HistoryDialogWindow>();
    }
}
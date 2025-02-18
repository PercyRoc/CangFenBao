using Presentation_BenFly.ViewModels.Dialogs;
using Wpf.Ui.Controls;
using Presentation_CommonLibrary.Services;

namespace Presentation_BenFly.Views.Dialogs;

public partial class SettingsDialog
{
    public SettingsDialog(INotificationService notificationService)
    {
        InitializeComponent();
        
        notificationService.Register("SettingWindowGrowl", GrowlPanel);
    }

    private SettingsDialogViewModel ViewModel => (SettingsDialogViewModel)DataContext;

    private void RootNavigation_OnNavigating(object sender, NavigatingCancelEventArgs e)
    {
        ViewModel.OnNavigating(e);
    }

    private void RootNavigation_OnNavigated(object sender, NavigatedEventArgs e)
    {
        ViewModel.OnNavigated(e);
    }
}
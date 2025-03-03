using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Presentation_CommonLibrary.Services;
using Serilog;

namespace Presentation_XinBa.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    private readonly IDialogService _dialogService;

    public MainWindow(IDialogService dialogService, INotificationService notificationService)
    {
        _dialogService = dialogService;
        InitializeComponent();

        // Register Growl container
        notificationService.Register("MainWindowGrowl", GrowlPanel);

        // Add title bar mouse event handler
        MouseDown += OnWindowMouseDown;
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            // Allow dragging the window when left button is pressed in the title bar area
            if (e.ChangedButton == MouseButton.Left && e.GetPosition(this).Y <= 32) DragMove();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred while dragging window");
        }
    }

    private async void MetroWindow_Closing(object sender, CancelEventArgs e)
    {
        try
        {
            e.Cancel = true;
            var result = await _dialogService.ShowIconConfirmAsync(
                "Are you sure you want to close the application?",
                "Close Confirmation",
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;
            e.Cancel = false;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred while closing the application");
            e.Cancel = true;
            await _dialogService.ShowErrorAsync("Error occurred while closing the application, please try again", "Error");
        }
    }
}
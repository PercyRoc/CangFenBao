using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Common.Services.Ui;
using SangNeng.ViewModels.Windows;
using Serilog;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace SangNeng.Views.Windows;

/// <summary>
///     Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    private readonly IDialogService _dialogService;

    public MainWindow(IDialogService dialogService, INotificationService notificationService)
    {
        _dialogService = dialogService;
        InitializeComponent();
        // 注册Growl容器
        notificationService.Register("MainWindowGrowl", GrowlPanel);
        // 添加标题栏鼠标事件处理
        MouseDown += OnWindowMouseDown;
    }

    private async void MetroWindow_Closing(object sender, CancelEventArgs e)
    {
        try
        {
            e.Cancel = true;
            var result = await _dialogService.ShowIconConfirmAsync(
                    "Are you sure you want to close the program?",
                    "Close Confirmation",
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;
            e.Cancel = false;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred while closing the program");
            e.Cancel = true;
            await _dialogService.ShowErrorAsync("Error occurred while closing the program, please try again", "Error");
        }
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            // 当在标题栏区域按下左键时允许拖动窗口
            if (e.ChangedButton == MouseButton.Left && e.GetPosition(this).Y <= 32) DragMove();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "拖动窗口时发生错误");
        }
    }

    private void BarcodeTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainWindowViewModel viewModel)
        {
            if (sender is System.Windows.Controls.TextBox textBox && !string.IsNullOrWhiteSpace(textBox.Text))
            {
                // 触发条码扫描事件
                viewModel.OnBarcodeScanned(null, textBox.Text);
                // 清空输入框
                textBox.Text = string.Empty;
            }
        }
    }
}
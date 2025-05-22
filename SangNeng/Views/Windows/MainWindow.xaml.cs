using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Common.Services.Ui;
using Serilog;
using Sunnen.ViewModels.Windows;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MessageBoxButton = System.Windows.MessageBoxButton;

namespace Sunnen.Views.Windows;

/// <summary>
///     Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    private bool _isFocusedByPreviewScanLogic = false; 

    public MainWindow(INotificationService notificationService)
    {
        InitializeComponent();
        // 注册Growl容器
        notificationService.Register("MainWindowGrowl", GrowlPanel);
        
        // 添加标题栏鼠标事件处理
        MouseDown += OnWindowMouseDown;
        // 注册全局文本输入事件
        PreviewTextInput += MainWindow_PreviewTextInput;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Log.Information("主窗口加载完成");
            
            // 根据当前屏幕的工作区自动计算并设置窗口位置和大小
            Left = SystemParameters.WorkArea.Left;
            Top = SystemParameters.WorkArea.Top;
            Width = SystemParameters.WorkArea.Width;
            Height = SystemParameters.WorkArea.Height;
            
            // Set initial focus to the window itself, away from the manual input box
            Keyboard.Focus(this); 
        
        }
        catch (Exception ex)
        {
            Log.Error(ex, "主窗口加载时发生错误");
        }
    }

    private void MetroWindow_Closing(object sender, CancelEventArgs e)
    {
        try
        {
            e.Cancel = true;
            var result = HandyControl.Controls.MessageBox.Show(
                "Are you sure you want to close the program?",
                "Close Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;
            e.Cancel = false;
            
            // 释放资源
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.Dispose();
            }
            
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred while closing the program");
            e.Cancel = true;
            HandyControl.Controls.MessageBox.Show(
                "Error occurred while closing the program, please try again",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }
    
    // Add the new handler for the manual input TextBox
    private async void ManualBarcodeTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not HandyControl.Controls.TextBox textBox) return;
        var barcode = textBox.Text;
        if (string.IsNullOrWhiteSpace(barcode)) return;
        
        if (barcode.StartsWith('"'))
        {
            barcode = barcode[1..];
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.ProcessBarcodeAsync(barcode);
        }
        Keyboard.Focus(this); 
    }

    // 新增：全局文本输入事件处理，输入@时自动聚焦到输入框
    private void MainWindow_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text == "\"")
        {
            _isFocusedByPreviewScanLogic = true; 
            ManualBarcodeTextBox.Clear(); 
            ManualBarcodeTextBox.Focus();
            e.Handled = true; 
        }
    }

    private void ManualBarcodeTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_isFocusedByPreviewScanLogic)
        {
            _isFocusedByPreviewScanLogic = false; 
            return; 
        }

        if (sender is HandyControl.Controls.TextBox textBox && !string.IsNullOrEmpty(textBox.Text))
        {
            textBox.Clear();
        }
    }
}
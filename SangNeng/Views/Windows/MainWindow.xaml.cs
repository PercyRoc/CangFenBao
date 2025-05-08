using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Scanner;
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
    private readonly IScannerService _scannerService;

    public MainWindow(INotificationService notificationService, IScannerService scannerService)
    {
        InitializeComponent();
        _scannerService = scannerService;
        
        // 注册Growl容器
        notificationService.Register("MainWindowGrowl", GrowlPanel);
        
        // 添加标题栏鼠标事件处理
        MouseDown += OnWindowMouseDown;
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
            
            // 设置扫码枪拦截模式和手动输入状态
            if (BlockScannerInput != null)
            {
                bool enableManualInput = BlockScannerInput.IsChecked ?? true;
                
                // 设置文本框为只读（如果禁用手动输入）或可编辑，并确保始终启用
                ManualBarcodeTextBox.IsEnabled = true;
                ManualBarcodeTextBox.IsReadOnly = !enableManualInput;
                
                // 根据手动输入模式设置扫码枪拦截状态
                _scannerService.InterceptAllInput = !enableManualInput; // 启用手动输入时不拦截键盘事件，禁用时拦截
                
                Log.Information("初始化手动输入模式: {0}, 扫码枪拦截: {1}", 
                    enableManualInput ? "启用" : "禁用",
                    enableManualInput ? "关闭" : "开启");
            }
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

    // Remove or comment out the old handler if no longer needed
    /*
    private async void BarcodeTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox textBox)
        {
            var barcode = textBox.Text;
            if (!string.IsNullOrWhiteSpace(barcode))
            {
                await ViewModel.ProcessBarcodeAsync(barcode);
                // Optionally clear the box after processing
                // textBox.Clear();
            }
        }
    }
    */
    
    // Add the new handler for the manual input TextBox
    private async void ManualBarcodeTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        // Explicitly use HandyControl.Controls.TextBox to resolve ambiguity
        if (e.Key != Key.Enter || sender is not HandyControl.Controls.TextBox textBox) return;
        var barcode = textBox.Text;
        if (string.IsNullOrWhiteSpace(barcode)) return;
        // Access ViewModel through DataContext
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.ProcessBarcodeAsync(barcode);
        }
        // Clear the manual input box after processing
        textBox.Clear(); 
        // Move focus away from the manual input box to the main window
        Keyboard.Focus(this);
    }
    
    // 拦截开关变更处理
    private void BlockScannerInput_CheckedChanged(object sender, RoutedEventArgs e)
    {
        try
        {
            if (BlockScannerInput == null) return;
            
            var enableManualInput = BlockScannerInput.IsChecked ?? true;
            
            // 设置文本框为只读（如果禁用手动输入）或可编辑，并确保始终启用
            ManualBarcodeTextBox.IsEnabled = true;
            ManualBarcodeTextBox.IsReadOnly = !enableManualInput;
            
            // 调整界面提示
            if (ManualBarcodeTextBox != null)
            {
                // HandyControl的TextBox使用hc:InfoElement.Placeholder附加属性设置占位符
                HandyControl.Controls.InfoElement.SetPlaceholder(ManualBarcodeTextBox, 
                    enableManualInput ? "Enter barcode manually and press Enter..." : "Manual input disabled");
            }
            
            // 根据手动输入模式设置扫码枪拦截状态
            _scannerService.InterceptAllInput = !enableManualInput; // 启用手动输入时不拦截键盘事件，禁用时拦截
            
            Log.Information("手动输入模式已{Mode}，输入框现在{Status}，扫码枪拦截已{Intercept}", 
                enableManualInput ? "启用" : "禁用",
                enableManualInput ? "可用" : "不可用",
                enableManualInput ? "关闭" : "开启");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "切换手动输入模式时发生错误");
        }
    }
}
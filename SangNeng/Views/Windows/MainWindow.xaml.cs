using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Common.Services.Ui;
using Serilog;
using Sunnen.ViewModels.Windows;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MessageBoxButton = System.Windows.MessageBoxButton;
using System.Windows.Controls;

namespace Sunnen.Views.Windows;

/// <summary>
///     Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    public MainWindow(INotificationService notificationService)
    {
        InitializeComponent();
        // 注册Growl容器
        notificationService.Register("MainWindowGrowl", GrowlPanel);
        
        // 添加标题栏鼠标事件处理
        MouseDown += OnWindowMouseDown;
        // 注册全局文本输入事件
        PreviewTextInput += MainWindow_PreviewTextInput;
        
        // 订阅ViewModel的UI请求事件
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // 取消订阅旧的ViewModel事件
        if (e.OldValue is MainWindowViewModel oldViewModel)
        {
            oldViewModel.RequestClearBarcodeInput -= OnRequestClearBarcodeInput;
            oldViewModel.RequestFocusBarcodeInput -= OnRequestFocusBarcodeInput;
            oldViewModel.RequestFocusToWindow -= OnRequestFocusToWindow;
        }
        
        // 订阅新的ViewModel事件
        if (e.NewValue is not MainWindowViewModel newViewModel) return;
        newViewModel.RequestClearBarcodeInput += OnRequestClearBarcodeInput;
        newViewModel.RequestFocusBarcodeInput += OnRequestFocusBarcodeInput;
        newViewModel.RequestFocusToWindow += OnRequestFocusToWindow;
    }

    private void OnRequestClearBarcodeInput()
    {
        Dispatcher.Invoke(ManualBarcodeTextBox.Clear);
    }

    private void OnRequestFocusBarcodeInput()
    {
        Dispatcher.Invoke(() =>
        {
            ManualBarcodeTextBox.Focus();
        });
    }

    private void OnRequestFocusToWindow()
    {
        Dispatcher.Invoke(() =>
        {
            Keyboard.Focus(this);
        });
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
                // 取消订阅事件
                viewModel.RequestClearBarcodeInput -= OnRequestClearBarcodeInput;
                viewModel.RequestFocusBarcodeInput -= OnRequestFocusBarcodeInput;
                viewModel.RequestFocusToWindow -= OnRequestFocusToWindow;
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
    
    // 重构：条码扫描处理 - MVVM模式
    private async void ManualBarcodeTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not HandyControl.Controls.TextBox textBox) return;

        // 等待一小段时间，确保所有字符都已输入到文本框
        await Task.Delay(50);

        // 委托给ViewModel处理条码完成
        if (DataContext is MainWindowViewModel viewModel)
        {
            // 通过统一的条码完成处理入口，确保新条码覆盖逻辑生效
            viewModel.HandleBarcodeCompleteCommand.Execute();
        }
        
        Keyboard.Focus(this); 
    }

    // 新增：处理文本框内容变化，用于手动清空时的缓存清空
    private void ManualBarcodeTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && ManualBarcodeTextBox.Text.Length == 0)
        {
            // 当用户手动删除输入框内容时，清空缓存
            viewModel.ClearBarcodeBuffer();
        }
    }

    // 重构：全局文本输入事件处理 - MVVM模式
    private void MainWindow_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;

        switch (e.Text)
        {
            case "@":
                // 扫码枪输入开始
                viewModel.HandleScanStartCommand.Execute();
                e.Handled = true;
                break;
            case "\r":
            case "\n":
                // 扫码枪输入结束
                viewModel.HandleBarcodeCompleteCommand.Execute();
                e.Handled = true;
                break;
        }
    }
}
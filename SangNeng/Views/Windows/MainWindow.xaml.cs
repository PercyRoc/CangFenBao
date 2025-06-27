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
    // 扫码枪输入字符常量
    private const string SCANNER_START_CHAR = "@";
    private const string SCANNER_END_CHAR_CR = "\r";
    private const string SCANNER_END_CHAR_LF = "\n";

    public MainWindow(INotificationService notificationService)
    {
        InitializeComponent();
        
        // 注册Growl容器
        notificationService.Register("MainWindowGrowl", GrowlPanel);
        
        // 添加标题栏鼠标事件处理
        MouseDown += OnWindowMouseDown;
        
        // 注册全局文本输入事件
        PreviewTextInput += MainWindow_PreviewTextInput;
        Log.Information("【扫码UI】全局文本输入事件已注册，扫码枪字符: 开始='{StartChar}', 结束CR='{EndCR}', 结束LF='{EndLF}'", 
            SCANNER_START_CHAR, SCANNER_END_CHAR_CR, SCANNER_END_CHAR_LF);
        
        // 订阅ViewModel的UI请求事件
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Log.Information("【扫码UI】DataContext变化事件触发");
        
        // 取消订阅旧的ViewModel事件
        if (e.OldValue is MainWindowViewModel oldViewModel)
        {
            oldViewModel.RequestClearBarcodeInput -= OnRequestClearBarcodeInput;
            oldViewModel.RequestFocusBarcodeInput -= OnRequestFocusBarcodeInput;
            oldViewModel.RequestFocusToWindow -= OnRequestFocusToWindow;
            Log.Information("【扫码UI】已取消订阅旧ViewModel的UI请求事件");
        }
        
        // 订阅新的ViewModel事件
        if (e.NewValue is not MainWindowViewModel newViewModel) 
        {
            Log.Warning("【扫码UI】新的DataContext不是MainWindowViewModel类型");
            return;
        }
        
        newViewModel.RequestClearBarcodeInput += OnRequestClearBarcodeInput;
        newViewModel.RequestFocusBarcodeInput += OnRequestFocusBarcodeInput;
        newViewModel.RequestFocusToWindow += OnRequestFocusToWindow;
        Log.Information("【扫码UI】已订阅新ViewModel的UI请求事件: 清空输入框、设置焦点到输入框、设置焦点到窗口");
    }

    private void OnRequestClearBarcodeInput()
    {
        Log.Information("【扫码UI】接收到清空条码输入框请求");
        Dispatcher.Invoke(() =>
        {
            var previousText = ManualBarcodeTextBox.Text;
            ManualBarcodeTextBox.Clear();
            Log.Information("【扫码UI】条码输入框已清空，之前内容: '{PreviousText}'", previousText);
        });
    }

    private void OnRequestFocusBarcodeInput()
    {
        Log.Information("【扫码UI】接收到设置焦点到条码输入框请求");
        Dispatcher.Invoke(() =>
        {
            var focusResult = ManualBarcodeTextBox.Focus();
            Log.Information("【扫码UI】条码输入框焦点设置{Result}: 当前文本: '{CurrentText}'", 
                focusResult ? "成功" : "失败", ManualBarcodeTextBox.Text);
        });
    }

    private void OnRequestFocusToWindow()
    {
        Log.Information("【扫码UI】接收到设置焦点回主窗口请求");
        Dispatcher.Invoke(() =>
        {
            var previousFocus = Keyboard.FocusedElement?.GetType().Name ?? "Unknown";
            var focusResult = Keyboard.Focus(this);
            Log.Information("【扫码UI】主窗口焦点设置{Result}: 之前焦点元素: {PreviousFocus}", 
                focusResult != null ? "成功" : "失败", previousFocus);
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

    /// <summary>
    /// 处理条码输入完成的通用逻辑
    /// </summary>
    private void ProcessBarcodeComplete()
    {
        Log.Information("【扫码UI】开始处理条码输入完成逻辑");
        var currentText = ManualBarcodeTextBox.Text;
        Log.Information("【扫码UI】当前输入框内容: '{CurrentText}', 长度: {Length}", currentText, currentText.Length);

        // 强制数据绑定立即将TextBox的值更新到ViewModel
        Log.Information("【扫码UI】开始强制数据绑定同步");
        var binding = ManualBarcodeTextBox.GetBindingExpression(TextBox.TextProperty);
        if (binding != null)
        {
            binding.UpdateSource();
            Log.Information("【扫码UI】数据绑定同步完成，已将UI文本更新到ViewModel");
        }
        else
        {
            Log.Warning("【扫码UI】未找到文本框的数据绑定表达式");
        }

        // 委托给ViewModel处理
        Log.Information("【扫码UI】委托ViewModel处理条码完成命令");
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.HandleBarcodeCompleteCommand.Execute();
            Log.Information("【扫码UI】ViewModel条码完成命令已执行");
        }
        else
        {
            Log.Error("【扫码UI】DataContext不是MainWindowViewModel类型，无法执行条码完成命令");
        }
        
        // 将焦点返回到窗口
        Log.Information("【扫码UI】设置焦点回主窗口");
        SetFocusToWindow();
        Log.Information("【扫码UI】条码输入完成处理结束");
    }

    /// <summary>
    /// 设置焦点到主窗口
    /// </summary>
    private void SetFocusToWindow()
    {
        var previousFocus = Keyboard.FocusedElement?.GetType().Name ?? "Unknown";
        var focusResult = Keyboard.Focus(this);
        Log.Information("【扫码UI】主窗口焦点设置{Result}: 之前焦点: {PreviousFocus}", 
            focusResult != null ? "成功" : "失败", previousFocus);
    }

    // 重构：条码扫描处理 - MVVM模式
    private void ManualBarcodeTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        Log.Information("【扫码UI】条码输入框按键事件: {Key}", e.Key);
        
        if (e.Key != Key.Enter || sender is not HandyControl.Controls.TextBox textBox) 
        {
            if (e.Key != Key.Enter)
            {
                Log.Information("【扫码UI】非回车键，继续输入");
            }
            return;
        }

        Log.Information("【扫码UI】检测到手动输入回车键，当前文本: '{CurrentText}'", textBox.Text);
        // 处理条码输入完成
        ProcessBarcodeComplete();
    }

    // 新增：处理文本框内容变化，用于手动清空时的缓存清空
    private void ManualBarcodeTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var currentText = ManualBarcodeTextBox.Text;
        Log.Information("【扫码UI】条码输入框内容变化: '{CurrentText}', 长度: {Length}", currentText, currentText.Length);
        
        if (DataContext is MainWindowViewModel viewModel && currentText.Length == 0)
        {
            Log.Information("【扫码UI】检测到输入框被清空，通知ViewModel清空缓存");
            // 当用户手动删除输入框内容时，清空缓存
            viewModel.ClearBarcodeBuffer();
        }
        else if (currentText.Length > 0)
        {
            Log.Information("【扫码UI】输入框有内容，当前字符数: {Length}", currentText.Length);
        }
    }

    // 重构：全局文本输入事件处理 - MVVM模式
    private void MainWindow_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        Log.Information("【扫码UI】全局文本输入事件: '{InputText}', 字符码: {CharCode}", 
            e.Text, e.Text.Length > 0 ? (int)e.Text[0] : 0);
        
        if (DataContext is not MainWindowViewModel viewModel) 
        {
            Log.Warning("【扫码UI】DataContext不是MainWindowViewModel类型，忽略输入");
            return;
        }

        switch (e.Text)
        {
            case SCANNER_START_CHAR:
                Log.Information("【扫码UI】检测到扫码枪开始字符(@)，触发扫码开始处理");
                // 扫码枪输入开始
                viewModel.HandleScanStartCommand.Execute();
                e.Handled = true;
                Log.Information("【扫码UI】扫码开始字符已处理，事件标记为已处理");
                break;
            case SCANNER_END_CHAR_CR:
                Log.Information("【扫码UI】检测到扫码枪结束字符(CR \\r)，触发条码完成处理");
                // 扫码枪输入结束
                e.Handled = true;

                // 处理条码输入完成
                ProcessBarcodeComplete();
                break;
            case SCANNER_END_CHAR_LF:
                Log.Information("【扫码UI】检测到扫码枪结束字符(LF \\n)，触发条码完成处理");
                // 扫码枪输入结束
                e.Handled = true;

                // 处理条码输入完成
                ProcessBarcodeComplete();
                break;
            default:
                // 普通字符输入，不需要特殊处理
                if (e.Text.Length == 1 && char.IsControl(e.Text[0]))
                {
                    Log.Information("【扫码UI】检测到控制字符: {ControlChar} (字符码: {CharCode})", 
                        e.Text, (int)e.Text[0]);
                }
                else if (!string.IsNullOrEmpty(e.Text))
                {
                    Log.Information("【扫码UI】检测到普通字符输入: '{NormalChar}'", e.Text);
                }
                break;
        }
    }
}
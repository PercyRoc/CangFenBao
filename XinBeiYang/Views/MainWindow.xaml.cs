using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Common.Services.Ui;
using XinBeiYang.Services;
using Serilog;
using MessageBox = HandyControl.Controls.MessageBox;

// 确保引用

namespace XinBeiYang.Views;

/// <summary>
///     Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    private readonly IImageStorageService _imageStorageService;

    public MainWindow(INotificationService notificationService, IImageStorageService imageStorageService)
    {
        InitializeComponent();

        // 注册Growl容器
        notificationService.Register("MainWindowGrowl", GrowlPanel);

        _imageStorageService = imageStorageService;

        // 添加标题栏鼠标事件处理
        MouseDown += OnWindowMouseDown;
        Closing += MainWindow_Closing;
        KeyDown += OnKeyDownForSelfTest;
        // Loaded 事件已在 XAML 中关联
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 根据当前屏幕的工作区自动计算并设置窗口位置和大小
        Left = SystemParameters.WorkArea.Left;
        Top = SystemParameters.WorkArea.Top;
        Width = SystemParameters.WorkArea.Width;
        Height = SystemParameters.WorkArea.Height;
    }

    private async void OnKeyDownForSelfTest(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == (ModifierKeys.Control | ModifierKeys.Alt)
            && e.Key == Key.T)
        {
            e.Handled = true;
            try
            {
                Log.Information("触发水印自测: Ctrl+Alt+T");
                await RunWatermarkSelfTestAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "执行水印自测时发生异常");
            }
        }
    }

    private static BitmapSource GenerateTestImage(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.DimGray, null, new Rect(0, 0, width, height));
            var pen = new Pen(Brushes.LightGray, 1);
            for (var x = 0; x < width; x += 80) dc.DrawLine(pen, new Point(x, 0), new Point(x, height));
            for (var y = 0; y < height; y += 80) dc.DrawLine(pen, new Point(0, y), new Point(width, y));
            var ft = new FormattedText(
                "Watermark Test",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei"),
                48,
                Brushes.White,
                1.0);
            dc.DrawText(ft, new Point(40, 40));
        }
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private async Task RunWatermarkSelfTestAsync()
    {
        var testImage = GenerateTestImage(1920, 1080);
        var now = DateTime.Now;
        var barcode = $"TEST-{now:HHmmss}";

        // 1) 仅保存原图
        var originalPath = await _imageStorageService.SaveOriginalAsync(testImage, barcode, now);
        Log.Information("自测-原图保存: {Path}", originalPath);

        // 2) 保存带水印
        var watermarkedPath = await _imageStorageService.SaveImageWithWatermarkAsync(testImage, barcode, 12.34, 10, 20, 30, now);
        Log.Information("自测-带水印保存: {Path}", watermarkedPath);
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

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        // 如果应用正在关闭阶段，不再弹出确认框
        if (Application.Current?.Dispatcher.HasShutdownStarted == true ||
            Application.Current?.Dispatcher.HasShutdownFinished == true)
            return;

        // 阻止默认关闭，我们将通过对话框决定
        e.Cancel = true;

        MessageBoxResult result;
        try
        {
            // 在窗口已加载且处于可见并且有可用句柄时，使用 HandyControl，并显式传入 owner
            if (IsLoaded && IsVisible && PresentationSource.FromVisual(this) != null)
            {
                result = HandyControl.Controls.MessageBox.Show(
                    this,
                    "确定要关闭程序吗？",
                    "关闭确认",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);
            }
            else
            {
                // 否则回退为系统 MessageBox，避免内部 Activate 失败
                result = System.Windows.MessageBox.Show(
                    "确定要关闭程序吗？",
                    "关闭确认",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);
            }
        }
        catch (Exception ex)
        {
            // 最终兜底：记录并回退到系统 MessageBox
            Log.Warning(ex, "显示关闭确认对话框失败，已回退到系统 MessageBox");
            result = System.Windows.MessageBox.Show(
                "确定要关闭程序吗？",
                "关闭确认",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
        }

        if (result == MessageBoxResult.OK)
        {
            Closing -= MainWindow_Closing; // 取消订阅以避免再次触发
            Application.Current?.Shutdown();
        }
    }
}
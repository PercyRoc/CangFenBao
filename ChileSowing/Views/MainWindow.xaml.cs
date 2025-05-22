using System.Windows;
using System.Windows.Input; // Required for MouseButtonEventArgs

namespace ChileSowing.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
        // 设置窗口加载时最大化
        Loaded += (_, _) => {
            WindowState = WindowState.Maximized;
        };
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
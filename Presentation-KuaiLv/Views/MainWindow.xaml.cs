using System.Windows;
using System.Windows.Input;

namespace Presentation_KuaiLv.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        
        // 添加标题栏拖动功能
        this.MouseLeftButtonDown += (s, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                this.DragMove();
            }
        };
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
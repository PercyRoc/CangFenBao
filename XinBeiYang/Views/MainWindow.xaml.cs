﻿using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Common.Services.Ui;
using Serilog;
using MessageBox = HandyControl.Controls.MessageBox;
// 确保引用

namespace XinBeiYang.Views;

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
        Closing += MainWindow_Closing;
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
        // 阻止默认关闭，我们将通过对话框决定
        e.Cancel = true;

        // 使用 HandyControl 的 MessageBox 进行确认 (使用简洁重载)
        var result = MessageBox.Show(
            "确定要关闭程序吗？",
            "关闭确认",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question // 使用 System.Windows 的图标枚举
        );

        if (result == MessageBoxResult.OK)
        {
            // 用户确认退出，允许关闭并关闭应用程序
            Dispatcher.Invoke(() =>
            {
                Closing -= MainWindow_Closing; // 取消订阅以避免再次触发
                Application.Current.Shutdown();
            });
        }
    }
}
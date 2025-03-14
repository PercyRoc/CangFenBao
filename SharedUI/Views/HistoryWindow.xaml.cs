using System.Windows.Input;
using Common.Services.Ui;

namespace SharedUI.Views;

/// <summary>
///     历史记录查询窗口
/// </summary>
public partial class HistoryWindow
{
    /// <summary>
    ///     构造函数
    /// </summary>
    public HistoryWindow(INotificationService notificationService)
    {
        InitializeComponent();

        // 注册通知面板
        notificationService.Register("HistoryWindowGrowl", GrowlPanel);

        // 添加标题栏鼠标事件
        TitleBarArea.MouseLeftButtonDown += TitleBarArea_MouseLeftButtonDown;
    }

    /// <summary>
    ///     标题栏鼠标左键按下事件处理
    /// </summary>
    private void TitleBarArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 如果按下的是鼠标左键，则拖动窗口
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
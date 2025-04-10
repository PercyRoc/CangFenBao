using Common.Services.Ui;

namespace SharedUI.Views;

/// <summary>
///     历史记录查询用户控件
/// </summary>
public partial class HistoryDialogView
{
    /// <summary>
    ///     构造函数
    /// </summary>
    public HistoryDialogView(INotificationService notificationService)
    {
        InitializeComponent();

        // 注册通知面板
        notificationService.Register("HistoryWindowGrowl", GrowlPanel);
    }
}
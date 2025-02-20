using System.Windows;
using Presentation_CommonLibrary.Services;

namespace CangFenBao_SangNeng.Views.Windows;

public partial class HistoryWindow
{
    public HistoryWindow(INotificationService notificationService)
    {
        InitializeComponent();
        
        notificationService.Register("HistoryWindowGrowl", GrowlPanel);
    }
} 
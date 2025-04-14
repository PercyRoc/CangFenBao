using System.Windows.Controls;
using System.Windows.Input;
using Common.Services.Ui;
using Serilog;

namespace SangNeng.Views.Windows;

public partial class HistoryControl
{
    public HistoryControl(INotificationService notificationService)
    {
        InitializeComponent();
        notificationService.Register("HistoryWindowGrowl", GrowlPanel);
    }
} 
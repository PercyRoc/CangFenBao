using System.Windows;
using Wpf.Ui.Controls;

namespace ShanghaiModuleBelt.Views;

/// <summary>
///     格口统计对话框
/// </summary>
internal partial class ChuteStatisticsDialog : FluentWindow
{
    public ChuteStatisticsDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    ///     关闭按钮点击事件
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
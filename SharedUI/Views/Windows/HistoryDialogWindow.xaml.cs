using System.Windows;
using System;

namespace SharedUI.Views.Windows;

/// <summary>
/// Interaction logic for HistoryDialogWindow.xaml
/// </summary>
public partial class HistoryDialogWindow : Window, IDialogWindow
{
    public HistoryDialogWindow()
    {
        InitializeComponent();
        Owner = Application.Current?.MainWindow;
    }
    
    public IDialogResult? Result { get; set; }
    
    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is IDialogAware dialogAwareViewModel)
        {
            dialogAwareViewModel.OnDialogClosed();
        }
        base.OnClosed(e);
    }
}
using System.Windows.Controls;
using ChileSowing.ViewModels;

namespace ChileSowing.Views;

public partial class ChuteDetailDialogView : UserControl
{
    public ChuteDetailDialogView(ChuteDetailDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
} 
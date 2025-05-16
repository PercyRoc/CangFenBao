using System.Windows;

namespace SowingWall.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {
        public MainWindow()
        {
            InitializeComponent();

            var screenWidth = SystemParameters.WorkArea.Width;
            var screenHeight = SystemParameters.WorkArea.Height;

            Width = screenWidth ;
            Height = screenHeight;

            Width = Math.Max(Width, MinWidth);
            Height = Math.Max(Height, MinHeight);
        }
    }
}
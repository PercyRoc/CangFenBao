using System.Windows.Input;

namespace SharedUI.Views.Windows
{
    /// <summary>
    /// HistoryWindow.xaml 的交互逻辑
    /// </summary>
    public partial class HistoryWindow : IDialogWindow
    {
        public HistoryWindow()
        {
            InitializeComponent();
        }

        // Add the event handler implementation
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        public IDialogResult? Result { get; set; }
    }
}
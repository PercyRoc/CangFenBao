using System.Windows;
using Prism.Services.Dialogs;

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

            // 订阅对话框关闭事件
            Closed += HistoryWindow_Closed;
        }

        private void HistoryWindow_Closed(object? sender, EventArgs e)
        {
            // 确保清理资源，防止内存泄漏
            if (DialogContent.Content is FrameworkElement { DataContext: IDialogAware dialogAware })
            {
                dialogAware.OnDialogClosed();
            }

            DialogContent.Content = null;
        }

        public IDialogResult? Result { get; set; }

        // 实现IDialogWindow接口的方法
        public new void Show()
        {
            Owner = Application.Current.MainWindow;
            ShowDialog();
        }

        // 设置对话框内容
        // 当Prism的DialogService创建并打开窗口时，会调用此方法设置内容
        object IDialogWindow.Content
        {
            get => DialogContent.Content;
            set => DialogContent.Content = value;
        }
    }
}
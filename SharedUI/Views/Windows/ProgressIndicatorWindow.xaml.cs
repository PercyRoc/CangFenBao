namespace SharedUI.Views.Windows
{
    /// <summary>
    /// 通用进度指示窗口的交互逻辑
    /// </summary>
    public partial class ProgressIndicatorWindow
    {
        public ProgressIndicatorWindow(string message = "正在处理，请稍候...")
        {
            InitializeComponent();
            MessageTextBlock.Text = message;
        }
    }
} 
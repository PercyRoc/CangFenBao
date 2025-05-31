namespace Common.Views.Settings.ChuteRules
{
    public partial class CustomDialogWindow : IDialogWindow
    {
        public CustomDialogWindow()
        {
            InitializeComponent();
        }

        public IDialogResult Result { get; set; } = null!;
    }
} 
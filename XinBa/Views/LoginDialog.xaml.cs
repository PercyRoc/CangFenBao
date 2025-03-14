using Prism.Services.Dialogs;
using Serilog;
using XinBa.ViewModels;

namespace XinBa.Views;

/// <summary>
///     LoginDialog.xaml 的交互逻辑
/// </summary>
internal partial class LoginDialog
{
    internal LoginDialog()
    {
        InitializeComponent();
        Log.Debug("LoginDialog初始化完成");

        // 订阅ViewModel的RequestClose事件
        Loaded += (_, _) =>
        {
            Log.Debug("LoginDialog已加载");

            if (DataContext is LoginViewModel viewModel)
            {
                Log.Debug("获取到LoginViewModel实例");
                viewModel.RequestClose += OnRequestClose;

                // 设置初始密码
                PasswordBox.Password = viewModel.Password;
                Log.Debug("已设置初始密码");

                // 添加密码变更事件
                PasswordBox.PasswordChanged += (_, _) =>
                {
                    viewModel.Password = PasswordBox.Password;
                    Log.Debug("密码已更新: {Length}字符", PasswordBox.Password.Length);
                };

                // 监听ViewModel密码变更
                viewModel.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(LoginViewModel.Password) ||
                        PasswordBox.Password == viewModel.Password) return;

                    PasswordBox.Password = viewModel.Password;
                    Log.Debug("从ViewModel更新密码: {Length}字符", viewModel.Password.Length);
                };

                Log.Debug("所有事件处理程序已注册");
            }
            else
            {
                Log.Warning("DataContext不是LoginViewModel类型");
            }
        };
    }

    private void OnRequestClose(IDialogResult result)
    {
        // 关闭窗口
        Log.Debug("收到关闭请求，结果: {Result}", result.Result);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        // 取消订阅事件
        Log.Debug("LoginDialog正在关闭");

        if (DataContext is LoginViewModel viewModel)
        {
            viewModel.RequestClose -= OnRequestClose;
            Log.Debug("已取消订阅RequestClose事件");
        }

        base.OnClosed(e);
    }
}
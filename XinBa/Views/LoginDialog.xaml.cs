// Remove Prism.Services.Dialogs using if IDialogAware logic is fully in ViewModels
// using Prism.Services.Dialogs;
using Serilog;
// Add using for UserControl
using XinBa.ViewModels;

namespace XinBa.Views;

/// <summary>
///     LoginDialog.xaml 的交互逻辑
/// </summary>
public partial class LoginDialog // Change base class if necessary (usually implicit from XAML)
{
    public LoginDialog()
    {
        InitializeComponent();
        Log.Debug("LoginDialog (UserControl) 初始化完成");

        // 订阅ViewModel的RequestClose事件 (REMOVED, Prism handles this)
        Loaded += (_, _) =>
        {
            if (DataContext is LoginViewModel viewModel)
            {
                Log.Debug("获取到LoginViewModel实例");
                // viewModel.RequestClose += OnRequestClose; // REMOVED

                // 设置初始密码
                PasswordBox.Password = viewModel.Password;
                Log.Debug("已设置初始密码");

                // 添加密码变更事件
                PasswordBox.PasswordChanged += (_, _) =>
                {
                    // Check if DataContext is still valid before accessing viewModel
                    if (DataContext is LoginViewModel currentViewModel)
                    {
                        currentViewModel.Password = PasswordBox.Password;
                        // Log.Debug("密码已更新: {Length}字符", PasswordBox.Password.Length); // Reduce log verbosity
                    }
                };

                // 监听ViewModel密码变更
                viewModel.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(LoginViewModel.Password) ||
                        PasswordBox.Password == viewModel.Password) return;

                    PasswordBox.Password = viewModel.Password;
                    Log.Debug("从ViewModel更新密码框"); // Simplified log message
                };

                Log.Debug("LoginDialog 事件处理程序已注册");
            }
            else
            {
                Log.Warning("DataContext不是LoginViewModel类型");
            }
        };

        // REMOVE Unloaded or OnClosed logic for unsubscribing RequestClose
        // Unloaded += (_, _) =>
        // {
        //     if (DataContext is LoginViewModel viewModel)
        //     {
        //         viewModel.RequestClose -= OnRequestClose; // REMOVED
        //         Log.Debug("LoginDialog (UserControl) 已卸载，取消订阅RequestClose事件");
        //     }
        // };
    }

    // Remove OnRequestClose method
    // private void OnRequestClose(IDialogResult result)
    // {
    //     // 关闭窗口 (REMOVED, Prism handles this)
    //     Log.Debug("收到关闭请求，结果: {Result}", result.Result);
    //     // Close();
    // }

    // Remove OnClosed override
    // protected override void OnClosed(EventArgs e)
    // {
    //     // 取消订阅事件
    //     Log.Debug("LoginDialog正在关闭");
    //
    //     if (DataContext is LoginViewModel viewModel)
    //     {
    //         viewModel.RequestClose -= OnRequestClose;
    //         Log.Debug("已取消订阅RequestClose事件");
    //     }
    //
    //     base.OnClosed(e);
    // }
}
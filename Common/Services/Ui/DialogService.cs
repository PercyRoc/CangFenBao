using System.Windows;
using Prism.Ioc;
using Prism.Services.Dialogs;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using HcMessageBox = HandyControl.Controls.MessageBox;
namespace Common.Services.Ui;

/// <summary>
///     对话框服务实现
/// </summary>
public class DialogService : IDialogService
{
    private readonly IContainerProvider _containerProvider;

    /// <summary>
    ///     构造函数
    /// </summary>
    public DialogService(IContainerProvider containerProvider)
    {
        _containerProvider = containerProvider;
    }

    /// <inheritdoc />
    public void ShowDialog(string name, IDialogParameters? parameters = null, Action<IDialogResult>? callback = null)
    {
        // 从容器中解析窗口
        var window = _containerProvider.Resolve<Window>(name) ?? throw new ArgumentException($"找不到名为 {name} 的窗口");

        // 获取ViewModel
        if (window.DataContext is not IDialogAware viewModel) return;

        // 设置参数
        viewModel.OnDialogOpened(parameters ?? new DialogParameters());

        // 处理关闭请求
        viewModel.RequestClose += result =>
        {
            // 调用关闭事件
            viewModel.OnDialogClosed();

            // 关闭窗口
            window.Close();

            // 调用回调
            callback?.Invoke(result);
        };

        // 显示窗口
        window.ShowDialog();
    }

    /// <inheritdoc />
    public Task ShowErrorAsync(string message, string? title = null)
    {
        return Application.Current.Dispatcher.InvokeAsync(() => { HcMessageBox.Error(message, title ?? "错误"); }).Task;
    }

    /// <inheritdoc />
    public Task<MessageBoxResult> ShowIconConfirmAsync(string message, string title, MessageBoxImage icon)
    {
        return Application.Current.Dispatcher.InvokeAsync(() => HcMessageBox.Show(message, title, MessageBoxButton.YesNo, icon)).Task;
    }
}
using System.Windows;
using Prism.Ioc;
using Prism.Services.Dialogs;

namespace Presentation_CommonLibrary.Services;

public class CustomDialogService(IContainerProvider containerProvider) : ICustomDialogService
{
    public void ShowDialog(string name, IDialogParameters? parameters = null, Action<IDialogResult>? callback = null)
    {
        // 从容器中解析窗口
        var window = containerProvider.Resolve<Window>(name) ?? throw new ArgumentException($"找不到名为 {name} 的窗口");

        // 获取ViewModel
        if (window.DataContext is not IDialogAware viewModel) return;
        // 设置参数
        viewModel.OnDialogOpened(parameters ?? new DialogParameters());

        // 处理关闭请求
        viewModel.RequestClose += result =>
        {
            callback?.Invoke(result);
            window.Close();
        };

        // 显示窗口
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }
}
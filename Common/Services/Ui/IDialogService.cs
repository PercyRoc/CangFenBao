using System.Windows;
using Prism.Services.Dialogs;

namespace Common.Services.Ui;

public interface IDialogService
{
    /// <summary>
    ///     显示自定义对话框
    /// </summary>
    /// <param name="name">对话框名称</param>
    /// <param name="parameters">对话框参数</param>
    /// <param name="callback">回调函数</param>
    void ShowDialog(string name, IDialogParameters? parameters = null, Action<IDialogResult>? callback = null);

    /// <summary>
    ///     显示错误消息框
    /// </summary>
    Task ShowErrorAsync(string message, string? title = null);

    /// <summary>
    ///     显示带图标的确认消息框
    /// </summary>
    /// <returns>用户选择的结果</returns>
    Task<MessageBoxResult> ShowIconConfirmAsync(string message, string title, MessageBoxImage icon);
}
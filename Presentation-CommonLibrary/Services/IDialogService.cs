using System.Windows;

namespace Presentation_CommonLibrary.Services;

public interface IDialogService
{
    /// <summary>
    ///     显示普通消息框
    /// </summary>
    Task ShowMessageAsync(string message, string? title = null);

    /// <summary>
    ///     显示错误消息框
    /// </summary>
    Task ShowErrorAsync(string message, string? title = null);

    /// <summary>
    ///     显示确认消息框
    /// </summary>
    /// <returns>用户选择的结果</returns>
    Task<MessageBoxResult> ShowConfirmAsync(string message, string? title = null);

    /// <summary>
    ///     显示带图标的确认消息框
    /// </summary>
    /// <returns>用户选择的结果</returns>
    Task<MessageBoxResult> ShowIconConfirmAsync(string message, string title, MessageBoxImage icon);

    /// <summary>
    ///     显示自定义按钮的消息框
    /// </summary>
    /// <returns>用户选择的结果</returns>
    Task<MessageBoxResult> ShowCustomAsync(string message, string title, string yesText, string noText,
        string cancelText, MessageBoxImage icon);
}
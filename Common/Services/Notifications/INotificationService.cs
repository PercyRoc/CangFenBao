using System.Windows;

namespace Common.Services.Notifications;

public interface INotificationService
{
    /// <summary>
    ///     显示成功通知
    /// </summary>
    /// <param name="message">消息内容</param>
    void ShowSuccess(string message);

    /// <summary>
    ///     显示错误通知
    /// </summary>
    /// <param name="message">消息内容</param>
    void ShowError(string message);

    /// <summary>
    ///     显示警告通知
    /// </summary>
    /// <param name="message">消息内容</param>
    void ShowWarning(string message);

    /// <summary>
    ///     在指定窗口显示成功通知
    /// </summary>
    /// <param name="message">消息内容</param>
    /// <param name="token">目标窗口的Token</param>
    void ShowSuccessWithToken(string message, string token);

    /// <summary>
    ///     在指定窗口显示错误通知
    /// </summary>
    /// <param name="message">消息内容</param>
    /// <param name="token">目标窗口的Token</param>
    void ShowErrorWithToken(string message, string token);

    /// <summary>
    ///     在指定窗口显示警告通知
    /// </summary>
    /// <param name="message">消息内容</param>
    /// <param name="token">目标窗口的Token</param>
    void ShowWarningWithToken(string? message, string token);

    /// <summary>
    ///     注册通知容器
    /// </summary>
    /// <param name="token">容器标识</param>
    /// <param name="element">容器元素</param>
    void Register(string token, FrameworkElement element);
}
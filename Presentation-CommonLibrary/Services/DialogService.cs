using System.Windows;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using HcMessageBox = HandyControl.Controls.MessageBox;

namespace Presentation_CommonLibrary.Services;

public class DialogService : IDialogService
{
    public Task ShowMessageAsync(string message, string? title = null)
    {
        Application.Current.Dispatcher.Invoke(() => { HcMessageBox.Show(message, title ?? "提示"); });
        return Task.CompletedTask;
    }

    public Task ShowErrorAsync(string message, string? title = null)
    {
        Application.Current.Dispatcher.Invoke(() => { HcMessageBox.Error(message, title ?? "错误"); });
        return Task.CompletedTask;
    }

    public Task<MessageBoxResult> ShowConfirmAsync(string message, string? title = null)
    {
        var result = Application.Current.Dispatcher.Invoke(() =>
            HcMessageBox.Show(message, title ?? "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question));
        return Task.FromResult(result);
    }

    public Task<MessageBoxResult> ShowIconConfirmAsync(string message, string title, MessageBoxImage icon)
    {
        var result =
            Application.Current.Dispatcher.Invoke(() =>
                HcMessageBox.Show(message, title, MessageBoxButton.YesNo, icon));
        return Task.FromResult(result);
    }

    public Task<MessageBoxResult> ShowCustomAsync(string message, string title, string yesText, string noText,
        string cancelText, MessageBoxImage icon)
    {
        var result = Application.Current.Dispatcher.Invoke(() =>
            HcMessageBox.Show(message, title, MessageBoxButton.YesNoCancel, icon));
        return Task.FromResult(result);
    }
}
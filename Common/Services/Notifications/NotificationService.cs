using System.Windows;
using System.Windows.Controls;
using HandyControl.Controls;
using HandyControl.Data;

namespace Common.Services.Notifications;

public class NotificationService : INotificationService
{
    private const string DefaultToken = "MainWindowGrowl";

    public void ShowSuccess(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Growl.Success(new GrowlInfo
            {
                Message = message,
                ShowDateTime = false,
                StaysOpen = false,
                WaitTime = 2,
                Token = DefaultToken
            });
        });
    }

    public void ShowError(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Growl.Error(new GrowlInfo
            {
                Message = message,
                ShowDateTime = false,
                StaysOpen = true,
                WaitTime = 3,
                Token = DefaultToken
            });
        });
    }

    public void ShowWarning(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Growl.Warning(new GrowlInfo
            {
                Message = message,
                ShowDateTime = false,
                StaysOpen = false,
                WaitTime = 3,
                Token = DefaultToken
            });
        });
    }

    public void ShowSuccessWithToken(string message, string token)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Growl.Success(new GrowlInfo
            {
                Message = message,
                ShowDateTime = false,
                StaysOpen = false,
                WaitTime = 2,
                Token = token
            });
        });
    }

    public void ShowErrorWithToken(string message, string token)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Growl.Error(new GrowlInfo
            {
                Message = message,
                ShowDateTime = false,
                StaysOpen = true,
                WaitTime = 3,
                Token = token
            });
        });
    }

    public void ShowWarningWithToken(string? message, string token)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Growl.Warning(new GrowlInfo
            {
                Message = message,
                ShowDateTime = false,
                StaysOpen = false,
                WaitTime = 3,
                Token = token
            });
        });
    }

    public void Register(string token, FrameworkElement element)
    {
        if (element is not Panel panel)
            throw new ArgumentException("元素必须是Panel类型", nameof(element));

        Application.Current.Dispatcher.Invoke(() => Growl.Register(token, panel));
    }
}
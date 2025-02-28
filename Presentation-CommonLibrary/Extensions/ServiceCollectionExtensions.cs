using Presentation_CommonLibrary.Services;
using Prism.Ioc;

namespace Presentation_CommonLibrary.Extensions;

public static class ServiceCollectionExtensions
{
    public static void AddPresentationCommonServices(this IContainerRegistry services)
    {
        // 注册对话框服务
        services.RegisterSingleton<ICustomDialogService, CustomDialogService>();

        // 注册通知服务
        services.RegisterSingleton<INotificationService, NotificationService>();

        // 注册通知服务
        services.RegisterSingleton<INotificationService, NotificationService>();
        services.RegisterSingleton<IDialogService, DialogService>();
    }
}
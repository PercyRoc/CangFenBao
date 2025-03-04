using Presentation_CommonLibrary.Services;
using Prism.Ioc;

namespace Presentation_CommonLibrary.Extensions;

public static class ServiceCollectionExtensions
{
    public static IContainerRegistry AddPresentationCommonServices(this IContainerRegistry services)
    {
        // 注册对话框服务
        services.RegisterSingleton<ICustomDialogService, CustomDialogService>();

        // 注册通知服务
        services.RegisterSingleton<INotificationService, NotificationService>();
        // 注册通知服务
        services.RegisterSingleton<IDialogService, DialogService>();
        return services;
    }
}
using Prism.Ioc;
using SharedUI.ViewModels.Settings;
using SharedUI.Views.Settings;

namespace SharedUI.Extensions;

public static class ServiceCollectionExtensions
{
    public static void AddShardUi(this IContainerRegistry services)
    {
        services.Register<CameraSettingsView>();
        services.Register<CameraSettingsViewModel>();
    }
}
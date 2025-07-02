using Common.Services.Settings;

namespace KuaiLv.Models.Settings.App;

/// <summary>
///     应用程序设置
/// </summary>
[Configuration("AppSettings")]
public class AppSettings
{
    /// <summary>
    ///     操作模式
    ///     0: 称重模式
    ///     1: 收货模式
    ///     2: 称重+收货模式
    /// </summary>
    public int OperationMode { get; set; } = 0;
}
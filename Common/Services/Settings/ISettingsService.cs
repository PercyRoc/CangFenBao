using System.ComponentModel.DataAnnotations;

namespace Common.Services.Settings;

/// <summary>
///     设置服务接口
/// </summary>
public interface ISettingsService : IDisposable
{
    /// <summary>
    ///     等待设置服务初始化完成
    /// </summary>
    Task WaitForInitializationAsync();

    /// <summary>
    ///     设置服务初始化完成事件
    /// </summary>
    event EventHandler? InitializationCompleted;

    /// <summary>
    ///     加载设置
    /// </summary>
    /// <typeparam name="T">设置类型</typeparam>
    /// <param name="key">设置键名（可选）</param>
    /// <param name="useCache">是否使用缓存</param>
    /// <returns>设置实例</returns>
    T LoadSettings<T>(string? key = null, bool useCache = true) where T : class, new();

    /// <summary>
    ///     保存配置
    /// </summary>
    /// <typeparam name="T">配置类型</typeparam>
    /// <param name="configuration">配置实例</param>
    /// <param name="validate">是否进行验证</param>
    /// <param name="throwOnError">验证失败时是否抛出异常</param>
    /// <returns>验证结果，如果没有错误或未验证则为空数组</returns>
    ValidationResult[] SaveSettings<T>(T configuration, bool validate = false, bool throwOnError = false)
        where T : class;
}
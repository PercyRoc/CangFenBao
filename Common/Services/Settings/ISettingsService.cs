using System.ComponentModel.DataAnnotations;

namespace Common.Services.Settings;

/// <summary>
///     设置服务接口
/// </summary>
public interface ISettingsService : IDisposable
{
    /// <summary>
    ///     订阅配置变更事件
    /// </summary>
    /// <typeparam name="T">配置类型</typeparam>
    /// <param name="handler">事件处理方法</param>
    void OnSettingsChanged<T>(Action<T>? handler) where T : class, new();

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

    /// <summary>
    ///     校验配置
    /// </summary>
    /// <typeparam name="T">配置类型</typeparam>
    /// <param name="settings">配置实例</param>
    /// <returns>校验结果，如果有错误则包含错误信息</returns>
    ValidationResult[] ValidateSettings<T>(T settings) where T : class;
}
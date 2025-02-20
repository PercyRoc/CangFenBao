namespace CommonLibrary.Services;

public interface ISettingsService
{
    /// <summary>
    ///     加载设置
    /// </summary>
    /// <param name="key">设置键名</param>
    /// <typeparam name="T">设置类型</typeparam>
    /// <returns>设置对象</returns>
    T LoadSettings<T>(string key) where T : new();

    /// <summary>
    ///     删除设置
    /// </summary>
    /// <param name="key">设置键名</param>
    void DeleteSettings(string key);

    /// <summary>
    ///     检查设置是否存在
    /// </summary>
    /// <param name="key">设置键名</param>
    /// <returns>是否存在</returns>
    bool HasSettings(string key);

    /// <summary>
    ///     注册配置类型
    /// </summary>
    /// <param name="key">配置键名</param>
    /// <typeparam name="T">配置类型</typeparam>
    void RegisterConfiguration<T>(string key) where T : class;

    /// <summary>
    ///     加载指定类型的配置
    /// </summary>
    T LoadConfiguration<T>() where T : class, new();

    /// <summary>
    ///     保存配置
    /// </summary>
    /// <param name="key">配置键名</param>
    /// <param name="configuration">配置对象</param>
    /// <typeparam name="T">配置类型</typeparam>
    void SaveConfiguration<T>(string key, T configuration) where T : class;

    /// <summary>
    ///     保存配置（使用ConfigurationAttribute中的Key）
    /// </summary>
    /// <param name="configuration">配置对象</param>
    /// <typeparam name="T">配置类型</typeparam>
    void SaveConfiguration<T>(T configuration) where T : class?;

    /// <summary>
    ///     重新加载所有配置
    /// </summary>
    void ReloadAll();
}
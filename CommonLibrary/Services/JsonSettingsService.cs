using System.IO;
using System.Reflection;
using System.Text.Json;
using CommonLibrary.Models.Settings;

namespace CommonLibrary.Services;

public class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Dictionary<Type, string> _configurationKeys = [];
    private readonly string _settingsDirectory;

    public JsonSettingsService(string settingsDirectory = "Settings")
    {
        _settingsDirectory = settingsDirectory;
        if (!Directory.Exists(_settingsDirectory)) Directory.CreateDirectory(_settingsDirectory);

        // 自动扫描并注册配置类
        RegisterConfigurationTypes();
    }

    /// <summary>
    ///     注册配置类型
    /// </summary>
    /// <param name="key">配置键名</param>
    /// <typeparam name="T">配置类型</typeparam>
    public void RegisterConfiguration<T>(string key) where T : class
    {
        _configurationKeys[typeof(T)] = key;
    }

    public T LoadSettings<T>(string key) where T : new()
    {
        var filePath = GetSettingsFilePath(key);
        if (!File.Exists(filePath)) return new T();

        var json = File.ReadAllText(filePath);
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? new T();
        }
        catch
        {
            return new T();
        }
    }

    public void DeleteSettings(string key)
    {
        var filePath = GetSettingsFilePath(key);
        if (File.Exists(filePath)) File.Delete(filePath);
    }

    public bool HasSettings(string key)
    {
        return File.Exists(GetSettingsFilePath(key));
    }

    public T LoadConfiguration<T>() where T : class, new()
    {
        if (!_configurationKeys.TryGetValue(typeof(T), out var key))
            throw new ArgumentException($"未找到类型 {typeof(T).Name} 的配置键名");

        return LoadSettings<T>(key);
    }

    public void SaveConfiguration<T>(string key, T configuration) where T : class
    {
        SaveToFile(key, configuration);
    }

    public void ReloadAll()
    {
        // 目前没有需要重新加载的内容
        // 如果将来需要在内存中缓存配置，可以在这里清除缓存
    }

    /// <summary>
    ///     扫描并注册所有标记了 ConfigurationAttribute 的配置类
    /// </summary>
    private void RegisterConfigurationTypes()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var assembly in assemblies)
            try
            {
                var configTypes = assembly.GetTypes()
                    .Where(t => t.GetCustomAttribute<ConfigurationAttribute>() != null);

                foreach (var type in configTypes)
                {
                    var attribute = type.GetCustomAttribute<ConfigurationAttribute>();
                    if (attribute != null) _configurationKeys[type] = attribute.Key;
                }
            }
            catch (Exception)
            {
                // 忽略无法加载的程序集
            }
    }

    private string GetSettingsFilePath(string key)
    {
        return Path.Combine(_settingsDirectory, $"{key}.json");
    }

    private void SaveToFile<T>(string key, T configuration) where T : class
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var filePath = GetSettingsFilePath(key);
        var json = JsonSerializer.Serialize(configuration, JsonOptions);
        File.WriteAllText(filePath, json);
    }
}
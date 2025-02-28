using System.IO;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommonLibrary.Models.Settings;

namespace CommonLibrary.Services;

public class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Dictionary<string, object> _configurationCache = [];
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

        try
        {
            var json = File.ReadAllText(filePath);
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
        _configurationCache.Remove(key);
    }

    public bool HasSettings(string key)
    {
        return File.Exists(GetSettingsFilePath(key));
    }

    public T LoadConfiguration<T>() where T : class, new()
    {
        var key = GetConfigurationKey<T>();

        // 尝试从缓存中获取
        if (_configurationCache.TryGetValue(key, out var cached) && cached is T config) return config;

        // 从文件加载
        var result = LoadSettings<T>(key);
        _configurationCache[key] = result;
        return result;
    }

    public void SaveConfiguration<T>(T configuration) where T : class?
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var key = GetConfigurationKey<T>();
        SaveToFile(key, configuration);
        _configurationCache[key] = configuration;
    }

    void ISettingsService.SaveConfiguration<T>(string key, T configuration)
    {
        SaveConfiguration(configuration);
    }

    public void ReloadAll()
    {
        _configurationCache.Clear();
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

    private string GetConfigurationKey<T>()
    {
        var type = typeof(T);
        if (!_configurationKeys.TryGetValue(type, out var key)) throw new ArgumentException($"未找到类型 {type.Name} 的配置键名");
        return key;
    }

    private string GetSettingsFilePath(string key)
    {
        return Path.Combine(_settingsDirectory, $"{key}.json");
    }

    private void SaveToFile<T>(string key, T configuration) where T : class?
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var filePath = GetSettingsFilePath(key);
        var json = JsonSerializer.Serialize(configuration, JsonOptions);
        File.WriteAllText(filePath, json);
    }
}
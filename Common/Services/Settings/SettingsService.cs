using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using System.Threading;

namespace Common.Services.Settings;

/// <summary>
///     基于.NET配置系统的设置服务
/// </summary>
public class SettingsService : ISettingsService
{
    private static readonly SemaphoreSlim _fileAccessLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Dictionary<string, object> _cachedSettings = [];
    private readonly Dictionary<Type, List<Action<object>>> _changeCallbacks = [];
    private readonly Dictionary<Type, IDisposable> _changeTokens = [];

    private readonly Dictionary<Type, string> _configurationKeys = [];
    private readonly Dictionary<Type, IConfiguration> _configurations = [];
    private readonly string _settingsDirectory;

    /// <summary>
    ///     初始化设置服务
    /// </summary>
    /// <param name="settingsDirectory">设置目录</param>
    public SettingsService(string settingsDirectory = "Settings")
    {
        _settingsDirectory = settingsDirectory;

        if (!Directory.Exists(_settingsDirectory)) Directory.CreateDirectory(_settingsDirectory);

        RegisterConfigurationTypes();
    }

    /// <summary>
    ///     加载设置
    /// </summary>
    /// <typeparam name="T">设置类型</typeparam>
    /// <param name="key">设置键名（可选）</param>
    /// <param name="useCache">是否使用缓存</param>
    /// <returns>设置实例</returns>
    public T LoadSettings<T>(string? key = null, bool useCache = true) where T : class, new()
    {
        var settingsKey = key ?? GetConfigurationKey<T>();

        if (useCache && _cachedSettings.TryGetValue(settingsKey, out var value) && value is T cachedSettings)
            return cachedSettings;

        var filePath = GetSettingsFilePath(settingsKey);
        
        _fileAccessLock.Wait();
        try
        {
            if (!File.Exists(filePath))
            {
                var newSettings = new T();
                if (useCache) _cachedSettings[settingsKey] = newSettings;
                return newSettings;
            }

            if (!_configurations.TryGetValue(typeof(T), out var configuration))
            {
                var builder = new ConfigurationBuilder()
                    .AddJsonFile(filePath, false, true);
                configuration = builder.Build();
                _configurations[typeof(T)] = configuration;

                SetupChangeToken<T>(configuration);
            }

            var settings = new T();
            configuration.Bind(settings);

            if (useCache) _cachedSettings[settingsKey] = settings;

            return settings;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"加载设置异常: {ex.Message}");
            var newSettings = new T();
            if (useCache) _cachedSettings[settingsKey] = newSettings;
            return newSettings;
        }
        finally
        {
            _fileAccessLock.Release();
        }
    }

    /// <summary>
    ///     注册配置变更回调
    /// </summary>
    /// <typeparam name="T">配置类型</typeparam>
    /// <param name="callback">回调方法</param>
    public void OnSettingsChanged<T>(Action<T>? callback) where T : class, new()
    {
        if (callback == null) return;

        var type = typeof(T);
        if (!_changeCallbacks.TryGetValue(type, out var value))
        {
            value = [];
            _changeCallbacks[type] = value;
        }

        value.Add(obj => callback((T)obj));
    }

    /// <summary>
    ///     保存配置
    /// </summary>
    /// <typeparam name="T">配置类型</typeparam>
    /// <param name="configuration">配置实例</param>
    /// <param name="validate">是否进行验证</param>
    /// <param name="throwOnError">验证失败时是否抛出异常</param>
    /// <returns>验证结果，如果没有错误或未验证则为空数组</returns>
    public ValidationResult[] SaveSettings<T>(T configuration, bool validate = false, bool throwOnError = false)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (validate)
        {
            var validationResults = ValidateSettings(configuration);

            if (validationResults.Length > 0)
            {
                if (throwOnError)
                    throw new ValidationException(
                        $"配置校验失败: {string.Join(", ", validationResults.Select(r => r.ErrorMessage))}");

                return validationResults;
            }
        }

        var key = GetConfigurationKey<T>();
        SaveToFile(key, configuration);
        _cachedSettings[key] = configuration;

        return [];
    }

    /// <summary>
    ///     校验配置
    /// </summary>
    /// <typeparam name="T">配置类型</typeparam>
    /// <param name="settings">配置实例</param>
    /// <returns>校验结果，如果有错误则包含错误信息</returns>
    public ValidationResult[] ValidateSettings<T>(T settings) where T : class
    {
        var results = new List<ValidationResult>();
        var validationContext = new ValidationContext(settings);
        Validator.TryValidateObject(settings, validationContext, results, true);
        return [.. results];
    }

    /// <summary>
    ///     释放资源
    /// </summary>
    public void Dispose()
    {
        foreach (var token in _changeTokens.Values) token.Dispose();

        _changeTokens.Clear();

        GC.SuppressFinalize(this);
    }

    private void SetupChangeToken<T>(IConfiguration configuration) where T : class, new()
    {
        if (_changeTokens.TryGetValue(typeof(T), out var oldToken))
        {
            oldToken.Dispose();
            _changeTokens.Remove(typeof(T));
        }

        var token = configuration.GetReloadToken();
        var registration = token.RegisterChangeCallback(_ =>
        {
            var settings = LoadSettings<T>(useCache: false);

            var key = GetConfigurationKey<T>();
            _cachedSettings[key] = settings;

            if (_changeCallbacks.TryGetValue(typeof(T), out var callbacks))
                foreach (var callback in callbacks)
                    callback(settings);

            SetupChangeToken<T>(configuration);
        }, null);

        _changeTokens[typeof(T)] = registration;
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

    /// <summary>
    ///     获取配置键名
    /// </summary>
    /// <typeparam name="T">配置类型</typeparam>
    /// <returns>配置键名</returns>
    private string GetConfigurationKey<T>()
    {
        var type = typeof(T);
        if (_configurationKeys.TryGetValue(type, out var key)) return key;

        var attribute = type.GetCustomAttribute<ConfigurationAttribute>();
        if (attribute != null)
        {
            key = attribute.Key;
            _configurationKeys[type] = key;
        }
        else
        {
            throw new ArgumentException($"未找到类型 {type.Name} 的配置键名");
        }

        return key;
    }

    /// <summary>
    ///     获取设置文件路径
    /// </summary>
    /// <param name="key">设置键名</param>
    /// <returns>文件路径</returns>
    private string GetSettingsFilePath(string key)
    {
        return Path.Combine(_settingsDirectory, $"{key}.json");
    }

    /// <summary>
    ///     保存配置到文件
    /// </summary>
    /// <param name="key">配置键名</param>
    /// <param name="configuration">配置对象</param>
    /// <typeparam name="T">配置类型</typeparam>
    private void SaveToFile<T>(string key, T configuration) where T : class?
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var filePath = GetSettingsFilePath(key);
        var json = JsonSerializer.Serialize(configuration, JsonOptions);
        
        _fileAccessLock.Wait();
        try
        {
            File.WriteAllText(filePath, json);
        }
        finally
        {
            _fileAccessLock.Release();
        }
    }
}
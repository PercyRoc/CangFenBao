using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace Common.Services.Settings;

/// <summary>
///     基于.NET配置系统的设置服务
/// </summary>
public class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ConcurrentDictionary<string, object> _cachedSettings = new();
    private readonly ConcurrentDictionary<Type, List<Action<object>>> _changeCallbacks = new();
    private readonly ConcurrentDictionary<Type, IDisposable> _changeTokens = new();
    private readonly ConcurrentDictionary<Type, string> _configurationKeys = new();
    private readonly ConcurrentDictionary<Type, IConfiguration> _configurations = new();
    private readonly string _settingsDirectory;
    private readonly TaskCompletionSource<bool> _initializationComplete = new();
    private readonly object _lock = new();

    /// <summary>
    ///     设置服务初始化完成事件
    /// </summary>
    public event EventHandler? InitializationCompleted;

    /// <summary>
    ///     初始化设置服务
    /// </summary>
    /// <param name="settingsDirectory">设置目录</param>
    public SettingsService(string settingsDirectory = "Settings")
    {
        _settingsDirectory = settingsDirectory;

        if (!Directory.Exists(_settingsDirectory)) Directory.CreateDirectory(_settingsDirectory);

        // 异步初始化
        Task.Run(InitializeAsync);
    }

    /// <summary>
    ///     等待设置服务初始化完成
    /// </summary>
    public Task WaitForInitializationAsync()
    {
        return _initializationComplete.Task;
    }

    /// <summary>
    ///     异步初始化设置服务
    /// </summary>
    private async Task InitializeAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                RegisterConfigurationTypes();
                // 预加载所有配置
                foreach (var type in _configurationKeys.Keys)
                {
                    var method = typeof(SettingsService)
                        .GetMethod(nameof(LoadSettings), [typeof(string), typeof(bool)])
                        ?.MakeGenericMethod(type);
                    method?.Invoke(this, [null, true]);
                }
            });

            _initializationComplete.TrySetResult(true);
            InitializationCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"设置服务初始化失败: {ex.Message}");
            _initializationComplete.TrySetException(ex);
        }
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
        if (!File.Exists(filePath))
        {
            var newSettings = new T();

            if (useCache) _cachedSettings[settingsKey] = newSettings;

            return newSettings;
        }

        try
        {
            if (!_configurations.TryGetValue(typeof(T), out var configuration))
            {
                lock (_lock)
                {
                    if (!_configurations.TryGetValue(typeof(T), out configuration))
                    {
                        var builder = new ConfigurationBuilder()
                            .AddJsonFile(filePath, false, true);
                        configuration = builder.Build();
                        _configurations[typeof(T)] = configuration;

                        SetupChangeToken<T>(configuration);
                    }
                }
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
        var callbacks = _changeCallbacks.GetOrAdd(type, _ => []);
        callbacks.Add(obj => callback((T)obj));
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
    public static ValidationResult[] ValidateSettings<T>(T settings) where T : class
    {
        var validationResults = new List<ValidationResult>();
        Validator.TryValidateObject(settings, new ValidationContext(settings), validationResults, true);
        return [.. validationResults];
    }

    /// <summary>
    ///     释放资源
    /// </summary>
    public void Dispose()
    {
        foreach (var token in _changeTokens.Values)
        {
            token.Dispose();
        }
        _changeTokens.Clear();
        _cachedSettings.Clear();
        _configurations.Clear();
        _changeCallbacks.Clear();
        _configurationKeys.Clear();
        GC.SuppressFinalize(this);
    }

    private void SetupChangeToken<T>(IConfiguration configuration) where T : class, new()
    {
        var type = typeof(T);
        var token = configuration.GetReloadToken();
        var disposable = token.RegisterChangeCallback(_ =>
        {
            var newSettings = LoadSettings<T>(null, false);
            if (_changeCallbacks.TryGetValue(type, out var callbacks))
            {
                foreach (var callback in callbacks)
                {
                    callback(newSettings);
                }
            }
        }, null);

        _changeTokens[type] = disposable;
    }

    /// <summary>
    ///     扫描并注册所有标记了 ConfigurationAttribute 的配置类
    /// </summary>
    private void RegisterConfigurationTypes()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location));

        foreach (var assembly in assemblies)
        {
            try
            {
                var types = assembly.GetExportedTypes()
                    .Where(t => t.IsClass && !t.IsAbstract && t.GetCustomAttribute<ConfigurationAttribute>() != null);

                foreach (var type in types)
                {
                    var attribute = type.GetCustomAttribute<ConfigurationAttribute>();
                    if (attribute != null)
                    {
                        _configurationKeys[type] = attribute.Key;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("扫描程序集 {Assembly} 时发生错误: {Message}", assembly.FullName, ex.Message);
            }
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
        if (_configurationKeys.TryGetValue(type, out var key))
        {
            return key;
        }

        var attribute = type.GetCustomAttribute<ConfigurationAttribute>();
        if (attribute != null)
        {
            key = attribute.Key;
            _configurationKeys[type] = key;
            return key;
        }

        return type.Name;
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
        var filePath = GetSettingsFilePath(key);
        var json = JsonSerializer.Serialize(configuration, JsonOptions);
        File.WriteAllText(filePath, json);
    }
}
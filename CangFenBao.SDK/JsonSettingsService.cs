using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Common.Services.Settings;
using Serilog;

namespace CangFenBao.SDK
{
    /// <summary>
    /// 基于JSON文件的设置服务实现，用于SDK配置管理。
    /// </summary>
    internal class JsonSettingsService(SdkConfig config) : ISettingsService
    {
        /// <summary>
        /// 加载指定类型的配置。
        /// </summary>
        /// <typeparam name="T">配置类型</typeparam>
        /// <param name="key">配置键（在此实现中不使用）</param>
        /// <param name="useCache">是否使用缓存（在此实现中不使用）</param>
        /// <returns>加载的配置对象</returns>
        public T LoadSettings<T>(string? key = null, bool useCache = true) where T : class, new()
        {
            var configPath = GetPathForType(typeof(T));
            
            if (string.IsNullOrEmpty(configPath))
            {
                Log.Warning("未找到类型 {TypeName} 的配置路径，返回默认实例", typeof(T).Name);
                return new T();
            }

            if (!File.Exists(configPath))
            {
                Log.Information("配置文件不存在: {ConfigPath}，为类型 {TypeName} 创建默认配置", configPath, typeof(T).Name);
                var defaultConfig = new T();
                
                try
                {
                    SaveSettings(defaultConfig);
                    Log.Information("默认配置已保存到: {ConfigPath}", configPath);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "保存默认配置失败: {ConfigPath}", configPath);
                }
                
                return defaultConfig;
            }

            try
            {
                Log.Debug("开始加载配置文件: {ConfigPath}，类型: {TypeName}", configPath, typeof(T).Name);
                
                var json = File.ReadAllText(configPath);
                var settings = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                });

                if (settings != null)
                {
                    Log.Information("配置加载成功: {ConfigPath}，类型: {TypeName}", configPath, typeof(T).Name);
                    return settings;
                }
                else
                {
                    Log.Warning("配置文件 {ConfigPath} 反序列化后为null，返回默认配置", configPath);
                    return new T();
                }
            }
            catch (JsonException jsonEx)
            {
                Log.Error(jsonEx, "配置文件 {ConfigPath} JSON格式无效，返回默认配置", configPath);
                return new T();
            }
            catch (IOException ioEx)
            {
                Log.Error(ioEx, "读取配置文件 {ConfigPath} 时发生I/O错误，返回默认配置", configPath);
                return new T();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "加载配置文件 {ConfigPath} 时发生未预期错误，返回默认配置", configPath);
                return new T();
            }
        }

        /// <summary>
        /// 保存配置到文件。
        /// </summary>
        /// <typeparam name="T">配置类型</typeparam>
        /// <param name="configuration">要保存的配置对象</param>
        /// <param name="validate">是否验证配置</param>
        /// <param name="throwOnError">验证失败时是否抛出异常</param>
        /// <returns>验证结果数组</returns>
        public ValidationResult[] SaveSettings<T>(T configuration, bool validate = false, bool throwOnError = false) where T : class
        {
            var configPath = GetPathForType(typeof(T));
            var validationResults = new List<ValidationResult>();

            if (string.IsNullOrEmpty(configPath))
            {
                var error = new ValidationResult($"未找到类型 {typeof(T).Name} 的配置路径");
                Log.Error("保存配置失败: {Error}", error.ErrorMessage);
                validationResults.Add(error);
                
                return throwOnError ? throw new InvalidOperationException(error.ErrorMessage) : validationResults.ToArray();

            }

            // 验证配置
            if (validate)
            {
                Log.Debug("开始验证配置，类型: {TypeName}", typeof(T).Name);
                var validationContext = new ValidationContext(configuration, null, null);
                Validator.TryValidateObject(configuration, validationContext, validationResults, true);

                if (validationResults.Count > 0)
                {
                    Log.Warning("配置验证失败，类型: {TypeName}，错误数量: {ErrorCount}", 
                        typeof(T).Name, validationResults.Count);
                    
                    foreach (var result in validationResults)
                    {
                        Log.Warning("验证错误: {ErrorMessage}", result.ErrorMessage);
                    }

                    if (!throwOnError) return validationResults.ToArray();
                    var errorMessage = string.Join("; ", validationResults.Select(vr => vr.ErrorMessage));
                    throw new ValidationException($"配置验证失败: {errorMessage}");

                }
                
                Log.Debug("配置验证通过，类型: {TypeName}", typeof(T).Name);
            }

            try
            {
                Log.Debug("开始保存配置到文件: {ConfigPath}，类型: {TypeName}", configPath, typeof(T).Name);
                
                // 确保目录存在
                var directory = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Log.Debug("创建配置目录: {Directory}", directory);
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(configuration, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                });

                File.WriteAllText(configPath, json);
                
                var fileInfo = new FileInfo(configPath);
                Log.Information("配置保存成功: {ConfigPath}，类型: {TypeName}，文件大小: {FileSize} 字节", 
                    configPath, typeof(T).Name, fileInfo.Length);
            }
            catch (UnauthorizedAccessException authEx)
            {
                Log.Error(authEx, "保存配置文件 {ConfigPath} 时访问被拒绝，检查文件权限", configPath);
                var error = new ValidationResult($"无权限写入配置文件: {configPath}");
                validationResults.Add(error);
                
                if (throwOnError)
                    throw;
            }
            catch (DirectoryNotFoundException dirEx)
            {
                Log.Error(dirEx, "保存配置文件 {ConfigPath} 时目录未找到", configPath);
                var error = new ValidationResult($"配置文件目录不存在: {configPath}");
                validationResults.Add(error);
                
                if (throwOnError)
                    throw;
            }
            catch (IOException ioEx)
            {
                Log.Error(ioEx, "保存配置文件 {ConfigPath} 时发生I/O错误", configPath);
                var error = new ValidationResult($"保存配置文件时发生I/O错误: {configPath}");
                validationResults.Add(error);
                
                if (throwOnError)
                    throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存配置文件 {ConfigPath} 时发生未预期错误", configPath);
                var error = new ValidationResult($"保存配置时发生错误: {ex.Message}");
                validationResults.Add(error);
                
                if (throwOnError)
                    throw;
            }

            return validationResults.ToArray();
        }

        /// <summary>
        /// 根据类型获取配置文件路径。
        /// </summary>
        /// <param name="type">配置类型</param>
        /// <returns>配置文件路径，如果未找到则返回null</returns>
        private string? GetPathForType(Type type)
        {
            var typeName = type.Name;
            Log.Debug("查找类型 {TypeName} 的配置路径", typeName);
            
            // 根据类型名称映射到配置路径
            var path = typeName switch
            {
                "HuaRaySettings" => config.HuaRayConfigPath,
                "SerialPortSettings" => config.SerialPortSettingsPath,
                "WeightServiceSettings" => config.WeightServiceSettingsPath,
                _ => null
            };
            
            if (path != null)
            {
                Log.Debug("找到类型 {TypeName} 的配置路径: {ConfigPath}", typeName, path);
            }
            else
            {
                Log.Debug("未找到类型 {TypeName} 的配置路径", typeName);
            }
            
            return path;
        }

        /// <summary>
        /// 释放资源（在此实现中无需操作）。
        /// </summary>
        public void Dispose()
        {
            Log.Debug("JsonSettingsService 正在释放资源");
            // 在这个实现中无需特殊的清理操作
        }
    }
} 
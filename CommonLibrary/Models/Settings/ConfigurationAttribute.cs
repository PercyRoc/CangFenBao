namespace CommonLibrary.Models.Settings;

/// <summary>
///     标记配置类的特性
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class ConfigurationAttribute(string key) : Attribute
{
    /// <summary>
    ///     配置键名
    /// </summary>
    public string Key { get; } = key;
}
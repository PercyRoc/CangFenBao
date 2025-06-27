using Common.Services.Settings;

namespace ChileSowing.Models;

/// <summary>
/// 语言设置模型
/// </summary>
[Configuration]
public class LanguageSettings
{
    /// <summary>
    /// 当前语言代码（如："en-US", "zh-CN"）
    /// </summary>
    public string CurrentLanguage { get; set; } = "en-US";

    /// <summary>
    /// 支持的语言列表
    /// </summary>
    public Dictionary<string, string> SupportedLanguages { get; set; } = new()
    {
        { "en-US", "English" },
        { "zh-CN", "简体中文" }
    };
} 
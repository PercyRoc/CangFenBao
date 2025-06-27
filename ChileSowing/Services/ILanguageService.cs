using System.Globalization;

namespace ChileSowing.Services;

/// <summary>
/// 语言服务接口
/// </summary>
public interface ILanguageService
{
    /// <summary>
    /// 当前语言
    /// </summary>
    CultureInfo CurrentLanguage { get; }

    /// <summary>
    /// 支持的语言列表
    /// </summary>
    Dictionary<string, string> SupportedLanguages { get; }

    /// <summary>
    /// 切换语言
    /// </summary>
    /// <param name="languageCode">语言代码</param>
    void ChangeLanguage(string languageCode);

    /// <summary>
    /// 语言变更事件
    /// </summary>
    event EventHandler<CultureInfo>? LanguageChanged;
} 
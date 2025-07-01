using System.Globalization;
using WPFLocalizeExtension.Engine;

namespace ChileSowing.Services;

/// <summary>
/// 本地化服务接口
/// </summary>
public interface ILocalizationService
{
    /// <summary>
    /// 当前语言
    /// </summary>
    string CurrentLanguage { get; }

    /// <summary>
    /// 切换语言
    /// </summary>
    /// <param name="culture">语言文化</param>
    void ChangeLanguage(string culture);

    /// <summary>
    /// 获取本地化字符串
    /// </summary>
    /// <param name="key">资源键</param>
    /// <returns>本地化字符串</returns>
    string GetString(string key);

    /// <summary>
    /// 语言变更事件
    /// </summary>
    event EventHandler<string>? LanguageChanged;
}

/// <summary>
/// 本地化服务实现
/// </summary>
public class LocalizationService : ILocalizationService
{
    private string _currentLanguage = "zh-CN";

    public string CurrentLanguage => _currentLanguage;

    public event EventHandler<string>? LanguageChanged;

    public void ChangeLanguage(string culture)
    {
        if (_currentLanguage == culture) return;

        _currentLanguage = culture;
        
        // 设置WPFLocalizeExtension的当前文化
        LocalizeDictionary.Instance.Culture = new CultureInfo(culture);
        
        // 设置应用程序的文化
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        Thread.CurrentThread.CurrentUICulture = new CultureInfo(culture);

        // 触发语言变更事件
        LanguageChanged?.Invoke(this, culture);
    }

    public string GetString(string key)
    {
        var result = LocalizeDictionary.Instance.GetLocalizedObject(
            "ChileSowing:Resources/Strings:" + key,
            null,
            CultureInfo.CurrentUICulture);
        return result?.ToString() ?? key;
    }

    /// <summary>
    /// 初始化本地化服务
    /// </summary>
    public static void Initialize()
    {
        // 设置默认语言为中文
        LocalizeDictionary.Instance.SetCurrentThreadCulture = true;
        LocalizeDictionary.Instance.Culture = new CultureInfo("zh-CN");
    }
} 
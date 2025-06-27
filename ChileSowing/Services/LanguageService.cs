using System.Globalization;
using ChileSowing.Models;
using Common.Services.Settings;
using Serilog;
using WPFLocalizeExtension.Engine;

namespace ChileSowing.Services;

/// <summary>
/// 语言服务实现
/// </summary>
public class LanguageService : ILanguageService
{
    private readonly ISettingsService _settingsService;
    private CultureInfo _currentLanguage;

    public LanguageService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        
        // 从配置中加载当前语言
        var languageSettings = _settingsService.LoadSettings<LanguageSettings>();
        _currentLanguage = new CultureInfo(languageSettings.CurrentLanguage);
        
        // 设置WPFLocalizeExtension的当前语言
        LocalizeDictionary.Instance.Culture = _currentLanguage;
        
        Log.Information("Language service initialized with language: {Language}", _currentLanguage.Name);
    }

    public CultureInfo CurrentLanguage => _currentLanguage;

    public Dictionary<string, string> SupportedLanguages => new()
    {
        { "en-US", "English" },
        { "zh-CN", "简体中文" }
    };

    public event EventHandler<CultureInfo>? LanguageChanged;

    public void ChangeLanguage(string languageCode)
    {
        try
        {
            var newCulture = new CultureInfo(languageCode);
            
            if (_currentLanguage.Name == newCulture.Name)
            {
                Log.Information("Language is already set to {Language}", languageCode);
                return;
            }

            // 更新当前语言
            _currentLanguage = newCulture;
            
            // 更新WPFLocalizeExtension
            LocalizeDictionary.Instance.Culture = newCulture;
            
            // 保存到配置
            var languageSettings = _settingsService.LoadSettings<LanguageSettings>();
            languageSettings.CurrentLanguage = languageCode;
            _settingsService.SaveSettings(languageSettings);
            
            // 触发事件
            LanguageChanged?.Invoke(this, newCulture);
            
            Log.Information("Language changed to: {Language}", languageCode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error changing language to {Language}", languageCode);
        }
    }
} 
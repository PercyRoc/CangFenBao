namespace Camera.Models.Settings;

public class LocalizedEnum<TEnum> where TEnum : Enum
{
    public TEnum Value { get; set; }
    public string LocalizedName { get; set; } = string.Empty;
}

public static class BarcodeCharacterTypeExtension
{
    private static List<LocalizedEnum<BarcodeCharacterType>>? _allTypes;

    public static List<LocalizedEnum<BarcodeCharacterType>> AllTypes
    {
        get
        {
            return _allTypes ??= Enum.GetValues<BarcodeCharacterType>() // Returns BarcodeCharacterType[]
                // .Cast<BarcodeCharacterType>() // Redundant when using Enum.GetValues<T>()
                .Select(e => new LocalizedEnum<BarcodeCharacterType>
                {
                    Value = e,
                    LocalizedName =
                        ResxLocalizationProviderHelper.GetLocalizedValue($"Enum_BarcodeCharacterType_{e}")
                })
                .ToList();
        }
    }
}
public static class ResxLocalizationProviderHelper
{
    public static string GetLocalizedValue(string key)
    {
        string uiCulture = WPFLocalizeExtension.Engine.LocalizeDictionary.Instance.Culture.ToString();
        string? value = null;

        try
        {
            // Ensure the assembly and dictionary name in the key are correct.
            // Format: "Assembly:Full.Resource.Name:Key"
            value = WPFLocalizeExtension.Engine.LocalizeDictionary.Instance.GetLocalizedObject(
                "Camera:Camera.Resources.CameraSettings:" + key, 
                null, 
                WPFLocalizeExtension.Engine.LocalizeDictionary.Instance.Culture) as string;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to get localized value for key {Key} with culture {Culture}. Ensure Assembly and Dictionary are correct in the resource key string.", key, uiCulture);
        }
        return value ?? key; // Fallback to key if not found
    }
}
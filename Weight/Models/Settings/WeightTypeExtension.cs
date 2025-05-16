using System;
using System.Collections.Generic;
using System.Linq;
using WPFLocalizeExtension.Engine;

namespace Weight.Models.Settings;

public static class WeightTypeExtension
{
    private static List<LocalizedEnum<WeightType>>? _allTypes;

    public static List<LocalizedEnum<WeightType>> AllTypes
    {
        get
        {
            return _allTypes ??= Enum.GetValues<WeightType>()
                .Select(e => new LocalizedEnum<WeightType>
                {
                    Value = e,
                    LocalizedName = GetLocalizedEnumName(e)
                })
                .ToList();
        }
    }

    private static string GetLocalizedEnumName(WeightType enumValue)
    {
        string resourceKey = $"WeightType_{enumValue}";
        string? localizedValue = LocalizeDictionary.Instance.GetLocalizedObject(
            resourceKey, 
            null, 
            LocalizeDictionary.Instance.Culture) as string;

        return !string.IsNullOrEmpty(localizedValue) ? localizedValue : enumValue.ToString();
    }
} 
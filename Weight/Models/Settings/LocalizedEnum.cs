using System;

namespace Weight.Models.Settings;

public class LocalizedEnum<TEnum> where TEnum : Enum
{
    public TEnum Value { get; set; }
    public string LocalizedName { get; set; } = string.Empty;
} 
using System.ComponentModel;

namespace CommonLibrary.Models.Settings.Weight;

/// <summary>
///     Weight Type
/// </summary>
public enum WeightType
{
    /// <summary>
    ///     Static Weight
    /// </summary>
    [Description("Static Weight")]
    Static,

    /// <summary>
    ///     Dynamic Weight
    /// </summary>
    [Description("Dynamic Weight")]
    Dynamic
} 
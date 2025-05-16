using System.ComponentModel;

namespace DeviceService.DataSourceDevices.Weight;

/// <summary>
///     称重模块 Type
/// </summary>
public enum WeightType
{
    /// <summary>
    ///     Static 称重模块
    /// </summary>
    [Description("Static 称重模块")] Static,

    /// <summary>
    ///     Dynamic 称重模块
    /// </summary>
    [Description("Dynamic 称重模块")] Dynamic
}
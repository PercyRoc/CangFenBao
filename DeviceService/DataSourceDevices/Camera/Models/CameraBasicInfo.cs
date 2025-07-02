using System.Diagnostics.CodeAnalysis;

namespace DeviceService.DataSourceDevices.Camera.Models;

/// <summary>
///     Represents basic information about a camera device.
/// </summary>
[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
public class CameraBasicInfo
{
    /// <summary>
    ///     Unique identifier for the camera.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     User-friendly name for the camera.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Optional model name of the camera.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    ///     Optional serial number of the camera.
    /// </summary>
    public string? SerialNumber { get; init; }
}
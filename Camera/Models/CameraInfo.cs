namespace Camera.Models;

/// <summary>
/// Represents basic information about a camera device.
/// </summary>
public class CameraInfo
{
    /// <summary>
    /// Unique identifier for the camera.
    /// </summary>
    public string? Id { get; set; } = string.Empty;

    /// <summary>
    /// User-friendly name of the camera.
    /// </summary>
    public string? Name { get; set; } = string.Empty;

    /// <summary>
    /// Camera model information.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Serial number of the camera.
    /// </summary>
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>
    /// Current status of the camera (e.g., Connected, Disconnected, Error).
    /// </summary>
    public string Status { get; set; } = "Unknown";
}
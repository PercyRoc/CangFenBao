using CommonLibrary.Models.Settings.Camera.Enums;
using Prism.Mvvm;

namespace CommonLibrary.Models.Settings.Camera;

public class DeviceCameraInfo : BindableBase
{
    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public int Index { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public CameraStatus Status { get; set; }
}
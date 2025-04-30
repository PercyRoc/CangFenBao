using System.Text.Json.Serialization;

namespace DeviceService.DataSourceDevices.Camera.Models.Camera;

public class DeviceCameraInfo : BindableBase
{
    private int _index;
    private string _ipAddress = string.Empty;
    private bool _isSelected;
    private string _macAddress = string.Empty;
    private string _model = string.Empty;
    private string _serialNumber = string.Empty;

    // 序列化的默认构造函数
    [JsonConstructor]
    public DeviceCameraInfo()
    {
    }

    // 复制构造函数
    public DeviceCameraInfo(DeviceCameraInfo other)
    {
        _isSelected = other._isSelected;
        _index = other._index;
        _ipAddress = other._ipAddress;
        _macAddress = other._macAddress;
        _serialNumber = other._serialNumber;
        _model = other._model;
    }

    [JsonPropertyName("IsSelected")]
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    [JsonPropertyName("Index")]
    public int Index
    {
        get => _index;
        set => SetProperty(ref _index, value);
    }

    [JsonPropertyName("IpAddress")]
    public string IpAddress
    {
        get => _ipAddress;
        set => SetProperty(ref _ipAddress, value);
    }

    [JsonPropertyName("MacAddress")]
    public string MacAddress
    {
        get => _macAddress;
        set => SetProperty(ref _macAddress, value);
    }

    [JsonPropertyName("SerialNumber")]
    public string SerialNumber
    {
        get => _serialNumber;
        set => SetProperty(ref _serialNumber, value);
    }

    [JsonPropertyName("Model")]
    public string Model
    {
        get => _model;
        set => SetProperty(ref _model, value);
    }
}
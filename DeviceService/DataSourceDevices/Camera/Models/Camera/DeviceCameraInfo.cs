using System.Text.Json.Serialization;
using DeviceService.DataSourceDevices.Camera.Models.Camera.Enums;
using MvCodeReaderSDKNet;
using Prism.Mvvm;

namespace DeviceService.DataSourceDevices.Camera.Models.Camera;

public class DeviceCameraInfo : BindableBase
{
    private int _index;
    private string _ipAddress = string.Empty;
    private bool _isSelected;
    private string _macAddress = string.Empty;
    private string _model = string.Empty;
    private string _serialNumber = string.Empty;
    private CameraStatus _status;

    // Default constructor for serialization
    [JsonConstructor]
    public DeviceCameraInfo()
    {
    }

    // Copy constructor
    public DeviceCameraInfo(DeviceCameraInfo other)
    {
        _isSelected = other._isSelected;
        _index = other._index;
        _ipAddress = other._ipAddress;
        _macAddress = other._macAddress;
        _serialNumber = other._serialNumber;
        _model = other._model;
        _status = other._status;
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

    [JsonPropertyName("Status")]
    public CameraStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>
    ///     从设备信息更新相机信息
    /// </summary>
    public void UpdateFromDeviceInfo(MvCodeReader.MV_CODEREADER_DEVICE_INFO deviceInfo)
    {
        if (deviceInfo.nTLayerType == MvCodeReader.MV_CODEREADER_GIGE_DEVICE)
        {
            // 从设备信息中获取IP地址和MAC地址
            IpAddress = $"{deviceInfo.nMacAddrHigh >> 24}.{(deviceInfo.nMacAddrHigh >> 16) & 0xFF}.{(deviceInfo.nMacAddrHigh >> 8) & 0xFF}.{deviceInfo.nMacAddrHigh & 0xFF}";
            MacAddress = $"{deviceInfo.nMacAddrHigh:X2}-{deviceInfo.nMacAddrLow:X2}";
            
            // 序列号和型号从设备信息中获取
            SerialNumber = deviceInfo.nDeviceType.ToString();
            Model = deviceInfo.nMajorVer.ToString();
            Status = CameraStatus.Online;
        }
    }
}
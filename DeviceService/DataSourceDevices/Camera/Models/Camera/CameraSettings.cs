// using System.Text.Json.Serialization;
// using Common.Services.Settings;
// using DeviceService.DataSourceDevices.Camera.Models.Camera.Enums;
//
// namespace DeviceService.DataSourceDevices.Camera.Models.Camera;
//
// /// <summary>
// ///     相机设置
// /// </summary>
// [Configuration("CameraSettings")]
// public class CameraSettings : BindableBase
// {
//     private bool _barcodeRepeatFilterEnabled;
//     [JsonPropertyName("BarcodeRepeatFilterEnabled")]
//     public bool BarcodeRepeatFilterEnabled
//     {
//         get => _barcodeRepeatFilterEnabled;
//         set => SetProperty(ref _barcodeRepeatFilterEnabled, value);
//     }
//
//     private int _repeatCount = 3;
//     [JsonPropertyName("RepeatCount")]
//     public int RepeatCount
//     {
//         get => _repeatCount;
//         set => SetProperty(ref _repeatCount, value);
//     }
//
//     private int _repeatTimeMs = 1000;
//     [JsonPropertyName("RepeatTimeMs")]
//     public int RepeatTimeMs
//     {
//         get => _repeatTimeMs;
//         set => SetProperty(ref _repeatTimeMs, value);
//     }
//
//     private bool _enableImageSaving;
//     [JsonPropertyName("EnableImageSaving")]
//     public bool EnableImageSaving
//     {
//         get => _enableImageSaving;
//         set => SetProperty(ref _enableImageSaving, value);
//     }
//
//     private string _imageSavePath = "Images";
//     [JsonPropertyName("ImageSavePath")]
//     public string ImageSavePath
//     {
//         get => _imageSavePath;
//         set => SetProperty(ref _imageSavePath, value);
//     }
//
//     private ImageFormat _imageFormat = ImageFormat.Jpeg;
//     [JsonPropertyName("ImageFormat")]
//     public ImageFormat ImageFormat
//     {
//         get => _imageFormat;
//         set => SetProperty(ref _imageFormat, value);
//     }
//
//     private string _hikvisionIp = "192.168.1.64";
//     [JsonPropertyName("HikvisionIp")]
//     public string HikvisionIp
//     {
//         get => _hikvisionIp;
//         set => SetProperty(ref _hikvisionIp, value);
//     }
//
//     private int _hikvisionPort = 8000;
//     [JsonPropertyName("HikvisionPort")]
//     public int HikvisionPort
//     {
//         get => _hikvisionPort;
//         set => SetProperty(ref _hikvisionPort, value);
//     }
//
//     private string _hikvisionUser = "admin";
//     [JsonPropertyName("HikvisionUser")]
//     public string HikvisionUser
//     {
//         get => _hikvisionUser;
//         set => SetProperty(ref _hikvisionUser, value);
//     }
//
//     private string _hikvisionPassword = "12345";
//     [JsonPropertyName("HikvisionPassword")]
//     public string HikvisionPassword
//     {
//         get => _hikvisionPassword;
//         set => SetProperty(ref _hikvisionPassword, value);
//     }
//
//     private int _volumeCameraFusionTimeMs = 500;
//     [JsonPropertyName("VolumeCameraFusionTimeMs")]
//     public int VolumeCameraFusionTimeMs
//     {
//         get => _volumeCameraFusionTimeMs;
//         set => SetProperty(ref _volumeCameraFusionTimeMs, value);
//     }
// }
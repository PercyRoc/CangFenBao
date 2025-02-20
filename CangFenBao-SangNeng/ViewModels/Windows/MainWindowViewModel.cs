using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommonLibrary.Models;
using CommonLibrary.Models.Settings.Camera;
using CommonLibrary.Models.Settings.Camera.Enums;
using CommonLibrary.Services;
using DeviceService.Camera;
using DeviceService.Camera.Hikvision;
using DeviceService.Camera.RenJia;
using DeviceService.Scanner;
using DeviceService.Weight;
using Presentation_CommonLibrary.Models;
using Presentation_CommonLibrary.Services;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CangFenBao_SangNeng.ViewModels.Windows;

/// <summary>
/// 主窗口视图模型
/// </summary>
public class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly ICustomDialogService _dialogService;
    private readonly IScannerService _scannerService;
    private readonly RenJiaCameraService _volumeCamera;
    private readonly ICameraService _cameraService;
    private readonly IWeightService _weightService;
    private readonly ISettingsService _settingsService;
    private readonly System.Timers.Timer _timer;
    private bool _disposed;
    private string _currentBarcode = string.Empty;
    private ImageSource? _currentImage;
    private ImageSource? _volumeImage;
    private ObservableCollection<PackageInfoItem> _packageInfoItems = [];
    private ObservableCollection<StatisticsItem> _statisticsItems = [];
    private ObservableCollection<PackageInfo> _packageHistory = [];
    private ObservableCollection<DeviceStatus> _deviceStatuses = [];
    private SystemStatus _systemStatus = SystemStatus.GetCurrentStatus();
    private readonly SemaphoreSlim _measurementLock = new(1, 1);
    private PackageInfo? _currentPackage;
    private readonly IPackageDataService _packageDataService;
    private int _packageIndex = 1;

    /// <summary>
    /// 构造函数
    /// </summary>
    public MainWindowViewModel(
        ICustomDialogService dialogService,
        IScannerService scannerService,
        RenJiaCameraService volumeCamera,
        ICameraService cameraService,
        IWeightService weightService,
        ISettingsService settingsService,
        IPackageDataService packageDataService)
    {
        _dialogService = dialogService;
        _scannerService = scannerService;
        _volumeCamera = volumeCamera;
        _cameraService = cameraService;
        _weightService = weightService;
        _settingsService = settingsService;
        _packageDataService = packageDataService;

        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        OpenHistoryWindowCommand = new DelegateCommand(() =>
        {
            _dialogService.ShowDialog("HistoryWindow");
        });
        InitializePackageInfoItems();
        InitializeStatisticsItems();
        InitializeDeviceStatuses();

        // 订阅扫码事件
        _scannerService.BarcodeScanned += OnBarcodeScanned;

        // 订阅体积相机连接状态
        _volumeCamera.ConnectionChanged += OnVolumeCameraConnectionChanged;

        // 订阅相机连接状态
        _cameraService.ConnectionChanged += OnCameraConnectionChanged;

        // 订阅重量称连接状态
        _weightService.ConnectionChanged += OnWeightScaleConnectionChanged;

        // 启动系统状态更新定时器
        _timer = new System.Timers.Timer(1000);
        _timer.Elapsed += (_, _) => { SystemStatus = SystemStatus.GetCurrentStatus(); };
        _timer.Start();
    }

    private static BitmapSource? ConvertToBitmapSource(Stream imageStream)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = imageStream;
            bitmap.EndInit();
            bitmap.Freeze(); // 使图像可以跨线程访问
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "转换图像时发生错误");
            return null;
        }
    }

    private static void UpdateImageDisplay(Image<Rgba32> image, Action<BitmapSource> imageUpdater)
    {
        try
        {
            // 创建内存流
            var memoryStream = new MemoryStream();
            image.SaveAsJpeg(memoryStream);
            memoryStream.Position = 0;

            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    var bitmapSource = ConvertToBitmapSource(memoryStream);
                    if (bitmapSource != null)
                    {
                        imageUpdater(bitmapSource);
                    }
                }
                finally
                {
                    memoryStream.Dispose();
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新图像显示时发生错误");
        }
    }

    private async void OnBarcodeScanned(object? sender, string barcode)
    {
        try
        {
            var items = PackageInfoItems.ToList();
            items[3].Value = DateTime.Now.ToString("HH:mm:ss");
            // 更新当前条码
            CurrentBarcode = barcode;
            Log.Information("收到扫码信息：{Barcode}", barcode);

            // 创建新的包裹对象
            _currentPackage = new PackageInfo
            {
                Index = _packageIndex++,
                Barcode = barcode,
                CreateTime = DateTime.Now,
                Status = PackageStatus.Created
            };
            
            // 获取重量数据
            var weight = _weightService.FindNearestWeight(_currentPackage.CreateTime);
            if (weight.HasValue)
            {
                _currentPackage.Weight = (float)(weight.Value / 1000); // 转换为千克
                Log.Information("获取到重量数据：{Weight:F3}kg", _currentPackage.Weight);
            }
            else
            {
                Log.Warning("未找到匹配的重量数据");
                _currentPackage.Weight = 0f;
            }
           
            items[0].Value = $"{weight}"; // 更新重量项
            PackageInfoItems = [.. items];
            
            // 触发体积测量
            await TriggerVolumeCamera();
            items[1].Value = $"{_currentPackage.Length}x{_currentPackage.Width}x{_currentPackage.Height}"; // 更新尺寸项
            
            // 触发相机拍照
            if (_cameraService is HikvisionIndustrialCameraSdkClient hikvisionCamera)
            {
                var image = hikvisionCamera.ExecuteSoftTrigger();
                if (image != null)
                {
                    Log.Information("触发相机拍照成功");
                    try
                    {
                        // 更新UI显示
                        UpdateImageDisplay(image, bitmap => CurrentImage = bitmap);

                        // 根据配置保存图像到文件
                        try
                        {
                            var cameraSettings = _settingsService.LoadSettings<CameraSettings>("CameraSettings");
                            SaveImage(image, cameraSettings, _currentPackage);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "保存图像到文件时发生错误");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "处理相机图像时发生错误");
                    }
                }
                else
                {
                    Log.Error("触发相机拍照失败");
                }
            }

            UpdatePackageStatus("Complete");

            // 添加到历史记录
            PackageHistory.Insert(0, _currentPackage);
            
            // 保存到数据库
            try
            {
                await _packageDataService.AddPackageAsync(_currentPackage);
                Log.Information("包裹数据已保存到数据库：{Barcode}", _currentPackage.Barcode);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存包裹数据到数据库时发生错误：{Barcode}", _currentPackage.Barcode);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理扫码信息时发生错误");
        }
    }

    private async Task TriggerVolumeCamera()
    {
        if (_currentPackage == null)
        {
            Log.Error("当前没有正在处理的包裹");
            return;
        }

        try
        {
            // 使用信号量确保同一时间只有一个测量任务在进行
            if (!await _measurementLock.WaitAsync(TimeSpan.FromSeconds(1)))
            {
                Log.Warning("上一次测量尚未完成，跳过本次测量");
                return;
            }

            try
            {
                _currentPackage.Status = PackageStatus.Measuring;

                // 触发测量
                var result = await Task.Run(() => _volumeCamera.TriggerMeasure());
                if (!result.IsSuccess)
                {
                    Log.Error("体积测量失败：{Error}", result.ErrorMessage);
                    _currentPackage.Status = PackageStatus.MeasureFailed;
                    _currentPackage.StatusDisplay = "测量失败";
                    _currentPackage.ErrorMessage = result.ErrorMessage;
                    return;
                }

                // 更新测量结果
                _currentPackage.Length = result.Length;
                _currentPackage.Width = result.Width;
                _currentPackage.Height = result.Height;
                _currentPackage.Volume = result.Length * result.Width * result.Height;
                _currentPackage.VolumeDisplay = $"{result.Length} × {result.Width} × {result.Height}";
                _currentPackage.Status = PackageStatus.MeasureSuccess;

                // 获取并显示图像
                var imageData = await Task.Run(() => _volumeCamera.GetMeasureImageFromId(result.ImageId));
                if (imageData != null)
                {
                    await UpdateImage(imageData);
                }
            }
            finally
            {
                _measurementLock.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "体积测量过程发生错误");
            if (_currentPackage != null)
            {
                _currentPackage.Status = PackageStatus.MeasureFailed;
                _currentPackage.ErrorMessage = ex.Message;
            }
        }
    }

    private void UpdatePackageStatus(string status)
    {
        var items = PackageInfoItems.ToList();
        items[3].Value = status; // 更新状态项
        items[2].Value = DateTime.Now.ToString("HH:mm:ss"); // 更新时间项
        PackageInfoItems = new ObservableCollection<PackageInfoItem>(items);
    }

    private async Task UpdateImage(byte[] imageData)
    {
        try
        {
            // 创建内存流的副本以确保数据在转换过程中不会被释放
            var memoryStream = new MemoryStream();
            await memoryStream.WriteAsync(imageData);
            memoryStream.Position = 0;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var bitmapSource = ConvertToBitmapSource(memoryStream);
                    if (bitmapSource != null)
                    {
                        VolumeImage = bitmapSource;
                    }
                }
                finally
                {
                    memoryStream.Dispose();
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新体积相机图像显示时发生错误");
        }
    }

    private void OnVolumeCameraConnectionChanged(string deviceId, bool isConnected)
    {
        try
        {
            var volumeCameraStatus = DeviceStatuses.FirstOrDefault(x => x.Name == "Volume Camera");
            if (volumeCameraStatus == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                volumeCameraStatus.Status = isConnected ? "Online" : "Offline";
                volumeCameraStatus.StatusColor = isConnected ? "#4CAF50" : "#FFA500";
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新体积相机状态时发生错误");
        }
    }

    private void OnCameraConnectionChanged(string deviceId, bool isConnected)
    {
        try
        {
            var cameraStatus = DeviceStatuses.FirstOrDefault(x => x.Name == "Photo Camera");
            if (cameraStatus == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                cameraStatus.Status = isConnected ? "Online" : "Offline";
                cameraStatus.StatusColor = isConnected ? "#4CAF50" : "#FFA500";
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新相机状态时发生错误");
        }
    }

    private void OnWeightScaleConnectionChanged(string deviceId, bool isConnected)
    {
        try
        {
            var weightScaleStatus = DeviceStatuses.FirstOrDefault(x => x.Name == "Weight Scale");
            if (weightScaleStatus == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                weightScaleStatus.Status = isConnected ? "Online" : "Offline";
                weightScaleStatus.StatusColor = isConnected ? "#4CAF50" : "#FFA500";
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新重量称状态时发生错误");
        }
    }

    #region Properties

    public string CurrentBarcode
    {
        get => _currentBarcode;
        private set => SetProperty(ref _currentBarcode, value);
    }

    public ImageSource? CurrentImage
    {
        get => _currentImage;
        private set => SetProperty(ref _currentImage, value);
    }

    public ImageSource? VolumeImage
    {
        get => _volumeImage;
        private set => SetProperty(ref _volumeImage, value);
    }

    public ObservableCollection<PackageInfoItem> PackageInfoItems
    {
        get => _packageInfoItems;
        private set => SetProperty(ref _packageInfoItems, value);
    }

    public ObservableCollection<StatisticsItem> StatisticsItems
    {
        get => _statisticsItems;
        private set => SetProperty(ref _statisticsItems, value);
    }

    public ObservableCollection<PackageInfo> PackageHistory
    {
        get => _packageHistory;
        set => SetProperty(ref _packageHistory, value);
    }

    public ObservableCollection<DeviceStatus> DeviceStatuses
    {
        get => _deviceStatuses;
        private set => SetProperty(ref _deviceStatuses, value);
    }

    public SystemStatus SystemStatus
    {
        get => _systemStatus;
        private set => SetProperty(ref _systemStatus, value);
    }

    public ICommand OpenSettingsCommand { get; }

    /// <summary>
    /// 打开历史记录窗口命令
    /// </summary>
    public ICommand OpenHistoryWindowCommand { get; }

    #endregion

    #region Private Methods

    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialog");
    }

    private void InitializePackageInfoItems()
    {
        PackageInfoItems =
        [
            new PackageInfoItem("Weight", "0.00", "kg", "Package Weight", "Scale24"),
            new PackageInfoItem("Size", "0 × 0 × 0", "mm", "Length × Width × Height", "Ruler24"),
            new PackageInfoItem("Time", "--:--:--", "", "Processing Time", "Timer24"),
            new PackageInfoItem("Status", "Waiting", "", "Processing Status", "AlertCircle24")
        ];
    }

    private void InitializeStatisticsItems()
    {
        StatisticsItems =
        [
            new StatisticsItem("Total", "0", "pcs", "Total Packages", "BoxMultiple24"),
            new StatisticsItem("Success", "0", "pcs", "Successfully Sorted", "CheckmarkCircle24"),
            new StatisticsItem("Failed", "0", "pcs", "Failed Packages", "ErrorCircle24"),
            new StatisticsItem("Rate", "0.00", "pcs/h", "Processing Rate", "ArrowTrendingLines24")
        ];
    }

    private void InitializeDeviceStatuses()
    {
        DeviceStatuses =
        [
            new DeviceStatus { Name = "Photo Camera", Status = "Offline", Icon = "Camera24", StatusColor = "#FFA500" },
            new DeviceStatus { Name = "Volume Camera", Status = "Offline", Icon = "Cube24", StatusColor = "#FFA500" },
            new DeviceStatus { Name = "Weight Scale", Status = "Offline", Icon = "Scale24", StatusColor = "#FFA500" }
        ];
    }

    private void SaveImage(Image<Rgba32> image, CameraSettings settings, PackageInfo package)
    {
        try
        {
            // 确保保存目录存在
            if (!Directory.Exists(settings.ImageSavePath))
            {
                Directory.CreateDirectory(settings.ImageSavePath);
            }

            // 生成文件名（使用条码和时间戳）
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var fileName = $"{package.Barcode}_{timestamp}";
            var extension = settings.ImageFormat switch
            {
                ImageFormat.Jpeg => ".jpg",
                ImageFormat.Png => ".png",
                ImageFormat.Bmp => ".bmp",
                ImageFormat.Tiff => ".tiff",
                _ => ".jpg"
            };
            var filePath = Path.Combine(settings.ImageSavePath, fileName + extension);

            // 保存图像
            switch (settings.ImageFormat)
            {
                case ImageFormat.Jpeg:
                    image.SaveAsJpeg(filePath);
                    break;
                case ImageFormat.Png:
                    image.SaveAsPng(filePath);
                    break;
                case ImageFormat.Bmp:
                    image.SaveAsBmp(filePath);
                    break;
                case ImageFormat.Tiff:
                    image.SaveAsTiff(filePath);
                    break;
                default:
                    image.SaveAsJpeg(filePath);
                    break;
            }

            // 保存图片路径到包裹对象
            package.ImagePath = filePath;

            Log.Debug("图像已保存：{FilePath}", filePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存图像时发生错误");
        }
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            // 停止定时器
            _timer.Stop();
            _timer.Dispose();

            // 取消事件订阅
            _scannerService.BarcodeScanned -= OnBarcodeScanned;
            _volumeCamera.ConnectionChanged -= OnVolumeCameraConnectionChanged;
            _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
            _weightService.ConnectionChanged -= OnWeightScaleConnectionChanged;

            // 释放信号量
            _measurementLock.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放资源时发生错误");
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
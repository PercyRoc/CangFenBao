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
using Presentation_SangNeng.Events;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Presentation_SangNeng.ViewModels.Settings;
using Prism.Events;

namespace Presentation_SangNeng.ViewModels.Windows;

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
    private ObservableCollection<SelectableTrayModel> _availableTrays;
    private SelectableTrayModel? _selectedTray;

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
        IPackageDataService packageDataService,
        IEventAggregator eventAggregator)
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

        // 订阅体积相机图像流
        _volumeCamera.ImageStream.Subscribe(imageData =>
        {
            try
            {
                Log.Debug("收到体积相机图像流数据，尺寸：{Width}x{Height}",
                    imageData.image.Width,
                    imageData.image.Height);

                UpdateImageDisplay(imageData.image, bitmap =>
                {
                    Log.Debug("从图像流更新VolumeImage");
                    VolumeImage = bitmap;
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理体积相机图像流数据时发生错误");
            }
        });

        // 订阅海康相机图像流
        if (_cameraService is HikvisionIndustrialCameraSdkClient hikvisionCamera)
        {
            hikvisionCamera.ImageStream.Subscribe(imageData =>
            {
                try
                {
                    Log.Debug("收到海康相机图像流数据，尺寸：{Width}x{Height}",
                        imageData.image.Width,
                        imageData.image.Height);

                    UpdateImageDisplay(imageData.image, bitmap =>
                    {
                        Log.Debug("从图像流更新CurrentImage");
                        CurrentImage = bitmap;
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理海康相机图像流数据时发生错误");
                }
            });
        }

        _availableTrays = new ObservableCollection<SelectableTrayModel>();
        
        SelectTrayCommand = new DelegateCommand<SelectableTrayModel>(ExecuteSelectTray);
        
        // 订阅托盘设置更改事件
        eventAggregator.GetEvent<TraySettingsChangedEvent>().Subscribe(LoadAvailableTrays);
        
        // Load available trays
        LoadAvailableTrays();
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
            Log.Error(ex, "转换图像流到BitmapSource时发生错误");
            return null;
        }
    }

    private static void UpdateImageDisplay(Image<Rgba32> image, Action<BitmapSource> imageUpdater)
    {
        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    using var memoryStream = new MemoryStream();
                    image.SaveAsJpeg(memoryStream);
                    memoryStream.Position = 0;

                    var bitmapSource = ConvertToBitmapSource(memoryStream);
                    if (bitmapSource != null)
                    {
                        imageUpdater(bitmapSource);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "在UI线程更新图像显示时发生错误");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新图像显示时发生错误");
        }
    }

    private void UpdateStatistics()
    {
        var items = StatisticsItems.ToList();
        
        // 更新总数
        items[0].Value = PackageHistory.Count.ToString();
        
        // 更新成功数
        var successCount = PackageHistory.Count(p => p.Status == PackageStatus.MeasureSuccess);
        items[1].Value = successCount.ToString();
        
        // 更新失败数
        var failedCount = PackageHistory.Count(p => p.Status == PackageStatus.MeasureFailed);
        items[2].Value = failedCount.ToString();
        
        // 计算处理速率（每小时）
        if (PackageHistory.Any())
        {
            var timeSpan = DateTime.Now - PackageHistory.Min(p => p.CreateTime);
            var ratePerHour = PackageHistory.Count / timeSpan.TotalHours;
            items[3].Value = $"{ratePerHour:F0}";
        }
        
        StatisticsItems = [.. items];
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

            // 触发相机拍照
            if (_cameraService is HikvisionIndustrialCameraSdkClient hikvisionCamera)
            {
                var image = hikvisionCamera.ExecuteSoftTrigger();
                if (image != null)
                {
                    Log.Information("触发相机拍照成功");
                    try
                    {
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

            // 获取重量数据
            // var weight = _weightService.FindNearestWeight(_currentPackage.CreateTime);
            // if (weight.HasValue)
            // {
            //     var finalWeight = weight.Value;
            //     // 如果选择了托盘且不是空托盘，减去托盘重量
            //     if (SelectedTray != null && SelectedTray.Name != "noTray")
            //     {
            //         finalWeight -= SelectedTray.Weight * 1000; // 转换为克
            //         Log.Information("减去托盘重量 {TrayWeight:F3}kg，实际重量 {FinalWeight:F3}kg", 
            //             SelectedTray.Weight, finalWeight / 1000);
            //     }
            //     _currentPackage.Weight = (float)(finalWeight / 1000); // 转换为千克
            //     Log.Information("获取到重量数据：{Weight:F3}kg", _currentPackage.Weight);
            // }
            // else
            // {
            //     Log.Warning("未找到匹配的重量数据");
            //     _currentPackage.Weight = 0f;
            // }
            //
            // items[0].Value = $"{weight}"; // 更新重量项
            // PackageInfoItems = [.. items];

            // 触发体积测量
            await TriggerVolumeCamera();

            // 处理体积数据
            if (_currentPackage.Status == PackageStatus.MeasureSuccess && SelectedTray != null && SelectedTray.Name != "noTray")
            {
                var originalLength = _currentPackage.Length;
                var originalWidth = _currentPackage.Width;
                var originalHeight = _currentPackage.Height;

                // 如果获取到的长度或宽度小于托盘尺寸，使用托盘尺寸
                if (originalLength < SelectedTray.Length || originalWidth < SelectedTray.Width)
                {
                    _currentPackage.Length = (int)Math.Max(originalLength ?? 0, SelectedTray.Length);
                    _currentPackage.Width = (int)Math.Max(originalWidth ?? 0, SelectedTray.Width);
                    Log.Information("使用托盘尺寸，长度：{Length}mm，宽度：{Width}mm", 
                        _currentPackage.Length, _currentPackage.Width);
                }

                // 高度始终要加上托盘高度
                _currentPackage.Height = originalHeight + SelectedTray.Height;
                Log.Information("加上托盘高度 {TrayHeight}mm，最终高度：{FinalHeight}mm", 
                    SelectedTray.Height, _currentPackage.Height);

                // 更新体积
                _currentPackage.Volume = _currentPackage.Length * _currentPackage.Width * _currentPackage.Height;
                _currentPackage.VolumeDisplay = $"{_currentPackage.Length} × {_currentPackage.Width} × {_currentPackage.Height}";
            }

            items[1].Value = $"{_currentPackage.Length}x{_currentPackage.Width}x{_currentPackage.Height}"; // 更新尺寸项
            UpdatePackageStatus("Complete");

            // 添加到历史记录
            PackageHistory.Insert(0, _currentPackage);
            
            // 更新统计信息
            UpdateStatistics();
            
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
                var result = await Task.Run(_volumeCamera.TriggerMeasure);
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
        PackageInfoItems = [.. items];
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
        private set
        {
            if (_volumeImage == value) return;
            if (SetProperty(ref _volumeImage, value))
            {
                Log.Debug("VolumeImage属性已更新");
            }
        }
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

    public ObservableCollection<SelectableTrayModel> AvailableTrays
    {
        get => _availableTrays;
        set => SetProperty(ref _availableTrays, value);
    }

    private SelectableTrayModel? SelectedTray
    {
        get => _selectedTray;
        set => SetProperty(ref _selectedTray, value);
    }

    public DelegateCommand<SelectableTrayModel> SelectTrayCommand { get; }

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

    private static void SaveImage(Image<Rgba32> image, CameraSettings settings, PackageInfo package)
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

    private void ExecuteSelectTray(SelectableTrayModel tray)
    {
        foreach (var availableTray in AvailableTrays)
        {
            availableTray.IsSelected = availableTray == tray;
        }

        SelectedTray = tray;
    }

    private void LoadAvailableTrays()
    {
        try
        {
            var traySettings = _settingsService.LoadConfiguration<TraySettings>();
            
            AvailableTrays.Clear();
            
            // 添加空托盘选项
            var emptyTray = new SelectableTrayModel(new TrayModel 
            { 
                Name = "noTray",
                Weight = 0,
                Length = 0,
                Width = 0,
                Height = 0
            });
            AvailableTrays.Add(emptyTray);

            // 添加配置的托盘
            foreach (var tray in traySettings.Trays)
            {
                AvailableTrays.Add(new SelectableTrayModel(tray));
            }

            // 默认选择空托盘
            emptyTray.IsSelected = true;
            SelectedTray = emptyTray;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载托盘配置失败");
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
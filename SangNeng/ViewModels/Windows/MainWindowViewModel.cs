using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Common.Data;
using Common.Models.Package;
using Common.Services.Audio;
using Common.Services.Settings;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Camera.Hikvision;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using DeviceService.DataSourceDevices.Camera.Models.Camera.Enums;
using DeviceService.DataSourceDevices.Camera.RenJia;
using DeviceService.DataSourceDevices.Scanner;
using DeviceService.DataSourceDevices.Weight;
using Presentation_SangNeng.Events;
using Presentation_SangNeng.ViewModels.Settings;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Serilog;
using SharedUI.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Color = System.Drawing.Color;
using Timer = System.Timers.Timer;

namespace Presentation_SangNeng.ViewModels.Windows;

/// <summary>
///     主窗口视图模型
/// </summary>
public class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly IAudioService _audioService;
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly SemaphoreSlim _measurementLock = new(1, 1);
    private readonly IPackageDataService _packageDataService;
    private readonly IScannerService _scannerService;
    private readonly ISettingsService _settingsService;
    private readonly Timer _timer;
    private readonly RenJiaCameraService _volumeCamera;
    private readonly SerialPortWeightService _weightService;
    private ObservableCollection<SelectablePalletModel> _availablePallets;
    private string _currentBarcode = string.Empty;
    private ImageSource? _currentImage;
    private PackageInfo? _currentPackage;
    private ObservableCollection<DeviceStatus> _deviceStatuses = [];
    private bool _disposed;
    private bool _hasPlayedErrorSound;
    private ObservableCollection<PackageInfo> _packageHistory = [];
    private int _packageIndex = 1;
    private ObservableCollection<PackageInfoItem> _packageInfoItems = [];
    private SelectablePalletModel? _selectedPallet;
    private ObservableCollection<StatisticsItem> _statisticsItems = [];
    private SystemStatus _systemStatus = SystemStatus.GetCurrentStatus();
    private ImageSource? _volumeImage;

    /// <summary>
    ///     构造函数
    /// </summary>
    public MainWindowViewModel(
        IDialogService dialogService,
        IScannerService scannerService,
        RenJiaCameraService volumeCamera,
        ICameraService cameraService,
        SerialPortWeightService weightService,
        ISettingsService settingsService,
        IPackageDataService packageDataService,
        IAudioService audioService,
        IEventAggregator eventAggregator)
    {
        _dialogService = dialogService;
        _scannerService = scannerService;
        _volumeCamera = volumeCamera;
        _cameraService = cameraService;
        _weightService = weightService;
        _settingsService = settingsService;
        _packageDataService = packageDataService;
        _audioService = audioService;

        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        OpenHistoryWindowCommand = new DelegateCommand(() => { _dialogService.ShowDialog("HistoryWindow"); });
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
        _timer = new Timer(1000);
        _timer.Elapsed += (_, _) => { SystemStatus = SystemStatus.GetCurrentStatus(); };
        _timer.Start();

        // 订阅体积相机图像流
        _volumeCamera.ImageStream
            .Sample(TimeSpan.FromMilliseconds(100)) // 限制刷新率，每秒最多10帧
            .Subscribe(imageData =>
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
            hikvisionCamera.ImageStream
                .Subscribe(imageData =>
                {
                    try
                    {
                        UpdateImageDisplay(imageData.image, bitmap => { CurrentImage = bitmap; });
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "处理海康相机图像流数据时发生错误");
                    }
                });

        _availablePallets = [];

        SelectPalletCommand = new DelegateCommand<SelectablePalletModel>(ExecuteSelectPallet);

        // 订阅托盘设置更改事件
        eventAggregator.GetEvent<PalletSettingsChangedEvent>().Subscribe(LoadAvailablePallets);

        // Load available pallets
        LoadAvailablePallets();
    }

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

    private static void UpdateImageDisplay(Image<Rgba32> image, Action<BitmapSource> imageUpdater)
    {
        try
        {
            // 创建仅在需要更新时才会执行的延迟操作
            var updateOperation = new Action(() =>
            {
                try
                {
                    // 对于大的图像进行缩小处理，提高显示性能
                    const int maxDisplayWidth = 1024; // 最大显示宽度
                    const int maxDisplayHeight = 768; // 最大显示高度

                    var width = image.Width;
                    var height = image.Height;
                    var needsResize = false;

                    // 计算缩放比例
                    if (width > maxDisplayWidth || height > maxDisplayHeight)
                    {
                        var scaleX = (double)maxDisplayWidth / width;
                        var scaleY = (double)maxDisplayHeight / height;
                        var scale = Math.Min(scaleX, scaleY);

                        width = (int)(width * scale);
                        height = (int)(height * scale);
                        needsResize = true;
                    }

                    var stride = width * 4; // RGBA每像素4字节
                    var pixelData = new byte[stride * height];

                    // 直接复制像素数据
                    if (needsResize)
                    {
                        // 使用SixLabors.ImageSharp进行缩放
                        using var resizedImage = image.Clone();
                        resizedImage.Mutate(x => x.Resize(width, height));
                        resizedImage.ProcessPixelRows(accessor =>
                        {
                            for (var y = 0; y < height; y++)
                            {
                                var row = accessor.GetRowSpan(y);
                                for (var x = 0; x < width; x++)
                                {
                                    var pixel = row[x];
                                    var offset = y * stride + x * 4;
                                    pixelData[offset] = pixel.B; // B
                                    pixelData[offset + 1] = pixel.G; // G
                                    pixelData[offset + 2] = pixel.R; // R
                                    pixelData[offset + 3] = pixel.A; // A
                                }
                            }
                        });
                    }
                    else
                    {
                        // 不需要缩放，直接复制
                        image.ProcessPixelRows(accessor =>
                        {
                            for (var y = 0; y < height; y++)
                            {
                                var row = accessor.GetRowSpan(y);
                                for (var x = 0; x < width; x++)
                                {
                                    var pixel = row[x];
                                    var offset = y * stride + x * 4;
                                    pixelData[offset] = pixel.B; // B
                                    pixelData[offset + 1] = pixel.G; // G
                                    pixelData[offset + 2] = pixel.R; // R
                                    pixelData[offset + 3] = pixel.A; // A
                                }
                            }
                        });
                    }

                    // 在UI线程创建WriteableBitmap
                    var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                    bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixelData, stride, 0);
                    bitmap.Freeze(); // 使图像可以跨线程访问

                    imageUpdater(bitmap);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "在UI线程更新图像显示时发生错误");
                }
            });

            // 将操作排队到UI线程
            Application.Current.Dispatcher.Invoke(updateOperation);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新图像显示时发生错误");
        }
    }

    private void UpdateStatistics()
    {
        Application.Current.Dispatcher.Invoke(() =>
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
        });
    }

    private async void OnBarcodeScanned(object? sender, string barcode)
    {
        try
        {
            // 重置错误音效播放标志
            _hasPlayedErrorSound = false;

            Application.Current.Dispatcher.Invoke(() =>
            {
                var items = PackageInfoItems.ToList();
                items[2].Value = DateTime.Now.ToString("HH:mm:ss");
                PackageInfoItems = [.. items];

                // 更新当前条码
                CurrentBarcode = barcode;
            });

            Log.Information("收到扫码信息：{Barcode}", barcode);

            // 创建新的包裹对象
            _currentPackage = new PackageInfo
            {
                Index = _packageIndex++,
                Barcode = barcode,
                CreateTime = DateTime.Now,
                Status = PackageStatus.Created
            };

            // 并行执行相机拍照、体积测量和重量获取，添加超时处理
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // 10秒超时
            try
            {
                // 创建三个并行任务
                Task<bool> photoTask;
                Image<Rgba32>? capturedImage = null;
                var imageCaptured = false;
                var imageLock = new object();
                IDisposable? imageSubscription = null;

                if (_cameraService is HikvisionIndustrialCameraSdkClient hikvisionCamera)
                {
                    // 先订阅图像流，准备接收触发后的图像
                    imageSubscription = hikvisionCamera.ImageStream.Subscribe(imageData =>
                    {
                        lock (imageLock)
                        {
                            if (imageCaptured) return;
                            capturedImage = imageData.image.Clone();
                            imageCaptured = true;
                            Log.Information("已捕获软触发图像");
                        }
                    });

                    // 然后执行软触发
                    photoTask = Task.Run(() =>
                    {
                        try
                        {
                            return hikvisionCamera.ExecuteSoftTrigger();
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "触发相机拍照失败");
                            PlayErrorSound();
                            return false;
                        }
                    }, cts.Token);
                }
                else
                {
                    photoTask = Task.FromResult(false);
                }

                var volumeTask = TriggerVolumeCamera(cts.Token);

                var weightTask = Task.Run(() =>
                {
                    try
                    {
                        var weight = _weightService.FindNearestWeight(DateTime.Now);
                        if (weight.HasValue)
                        {
                            // 计算实际重量（减去托盘重量）
                            var actualWeight = weight.Value / 1000;
                            if (SelectedPallet != null && SelectedPallet.Name != "noPallet")
                                actualWeight = Math.Max(0, actualWeight - SelectedPallet.Weight);
                            _currentPackage.Weight = (float)actualWeight;

                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                var items = PackageInfoItems.ToList();
                                items[0].Value = $"{_currentPackage.Weight:F3}";
                                PackageInfoItems = [.. items];
                            });
                        }
                        else
                        {
                            Log.Warning("未能获取到包裹重量");
                        }

                        return weight;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "获取重量数据失败");
                        return null;
                    }
                }, cts.Token);

                // 等待所有任务完成
                await Task.WhenAll(photoTask, volumeTask, weightTask);

                // 等待一小段时间，确保图像被捕获
                if (await photoTask && !imageCaptured)
                {
                    Log.Debug("软触发成功，等待图像数据...");
                    await Task.Delay(5000, cts.Token); // 等待5秒钟
                }

                // 处理体积数据
                if (_currentPackage.Status == PackageStatus.MeasureSuccess && SelectedPallet != null &&
                    SelectedPallet.Name != "noPallet")
                {
                    var originalLength = _currentPackage.Length;
                    var originalWidth = _currentPackage.Width;
                    var originalHeight = _currentPackage.Height;

                    // 托盘尺寸已经是厘米单位，直接使用
                    var palletLength = SelectedPallet.Length;
                    var palletWidth = SelectedPallet.Width;
                    var palletHeight = SelectedPallet.Height;

                    // 如果获取到的长度或宽度小于托盘尺寸，使用托盘尺寸
                    if (originalLength < palletLength || originalWidth < palletWidth)
                    {
                        _currentPackage.Length = Math.Max(originalLength ?? 0, palletLength);
                        _currentPackage.Width = Math.Max(originalWidth ?? 0, palletWidth);
                    }

                    // 高度始终要加上托盘高度
                    _currentPackage.Height = originalHeight + palletHeight;
                    // 更新体积（使用cm³作为单位）
                    _currentPackage.Volume = _currentPackage.Length * _currentPackage.Width * _currentPackage.Height;
                    // 显示尺寸（厘米）
                    _currentPackage.VolumeDisplay =
                        $"{_currentPackage.Length:F1}cm × {_currentPackage.Width:F1}cm × {_currentPackage.Height:F1}cm";

                    // 播放成功音效
                    _ = _audioService.PlayPresetAsync(AudioType.Success);
                }

                // 在体积测量完成后保存捕获的图像
                lock (imageLock)
                {
                    if (capturedImage != null)
                        try
                        {
                            var cameraSettings = _settingsService.LoadSettings<CameraSettings>("CameraSettings");
                            _ = SaveImageAsync(capturedImage, cameraSettings, _currentPackage, cts.Token);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "保存图像到文件时发生错误");
                        }
                    else
                        Log.Warning("未能捕获到软触发图像");
                }

                // 清理订阅
                imageSubscription?.Dispose();

                Application.Current.Dispatcher.Invoke(() =>
                {
                    var items = PackageInfoItems.ToList();
                    // 更新尺寸项，转换为cm显示
                    items[1].Value =
                        $"{_currentPackage.Length:F1}x{_currentPackage.Width:F1}x{_currentPackage.Height:F1}";
                    PackageInfoItems = [.. items];
                });

                UpdatePackageStatus("Complete");

                // 添加到历史记录
                Application.Current.Dispatcher.Invoke(() =>
                {
                    PackageHistory.Insert(0, _currentPackage);

                    // 更新统计信息
                    UpdateStatistics();
                });

                // 在后台保存到数据库，不等待完成
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _packageDataService.AddPackageAsync(_currentPackage);
                        Log.Information("包裹数据已保存到数据库：{Barcode}", _currentPackage.Barcode);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "保存包裹数据到数据库时发生错误：{Barcode}", _currentPackage.Barcode);
                        Application.Current.Dispatcher.Invoke(() => UpdatePackageStatus("Database Error"));
                    }
                }, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Warning("处理包裹超时：{Barcode}", barcode);
                UpdatePackageStatus("Timeout");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理扫码信息时发生错误");
            PlayErrorSound();
        }
    }

    private void PlayErrorSound()
    {
        if (_hasPlayedErrorSound) return;
        _hasPlayedErrorSound = true;
        _ = _audioService.PlayPresetAsync(AudioType.SystemError);
    }

    private async Task TriggerVolumeCamera(CancellationToken cancellationToken)
    {
        if (_currentPackage == null)
        {
            Log.Error("当前没有正在处理的包裹");
            return;
        }

        try
        {
            // 使用信号量确保同一时间只有一个测量任务在进行
            if (!await _measurementLock.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken))
            {
                Log.Warning("上一次测量尚未完成，跳过本次测量");
                PlayErrorSound();
                return;
            }

            try
            {
                _currentPackage.Status = PackageStatus.Measuring;

                // 触发测量
                var result = await Task.Run(_volumeCamera.TriggerMeasure, cancellationToken);
                if (!result.IsSuccess)
                {
                    Log.Error("体积测量失败：{Error}", result.ErrorMessage);
                    _currentPackage.Status = PackageStatus.MeasureFailed;
                    _currentPackage.StatusDisplay = "测量失败";
                    _currentPackage.ErrorMessage = result.ErrorMessage;
                    PlayErrorSound();
                    return;
                }

                // 更新测量结果（将毫米转换为厘米）
                _currentPackage.Length = result.Length / 10.0;
                _currentPackage.Width = result.Width / 10.0;
                _currentPackage.Height = result.Height / 10.0;
                _currentPackage.Volume = result.Length * result.Width * result.Height;
                _currentPackage.VolumeDisplay =
                    $"{_currentPackage.Length:F1}cm × {_currentPackage.Width:F1}cm × {_currentPackage.Height:F1}cm";
                _currentPackage.Status = PackageStatus.MeasureSuccess;
            }
            finally
            {
                _measurementLock.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "体积测量过程发生错误");
            if (_currentPackage != null)
            {
                _currentPackage.Status = PackageStatus.MeasureFailed;
                _currentPackage.ErrorMessage = ex.Message;
                PlayErrorSound();
            }
        }
    }

    private static async Task SaveImageAsync(Image<Rgba32> image, CameraSettings settings, PackageInfo package,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(async () =>
            {
                // 确保保存目录存在
                if (!Directory.Exists(settings.ImageSavePath)) Directory.CreateDirectory(settings.ImageSavePath);

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

                // 先保存原始图像到临时文件
                var tempFilePath = Path.Combine(settings.ImageSavePath, $"temp_{fileName}{extension}");
                await using (var fileStream =
                             new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await image.SaveAsJpegAsync(fileStream, cancellationToken);
                }

                // 使用 System.Drawing 添加水印
                using (var bitmap = new Bitmap(tempFilePath))
                using (var graphics = Graphics.FromImage(bitmap))
                using (var font = new Font("Arial", 40))
                using (var brush = new SolidBrush(Color.Green))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                    var watermarkLines = new[]
                    {
                        $"Barcode: {package.Barcode}",
                        $"Size: {package.Length:F1}cm × {package.Width:F1}cm × {package.Height:F1}cm",
                        $"Weight: {package.Weight:F3}kg",
                        $"Volume: {package.Volume / 1000.0:N0}cm³",
                        $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
                    };

                    var padding = 20;
                    var lineSpacing = 50;
                    var startY = padding;

                    foreach (var line in watermarkLines)
                    {
                        graphics.DrawString(line, font, brush, padding, startY);
                        startY += lineSpacing;
                    }

                    // 保存带水印的图像
                    bitmap.Save(filePath, System.Drawing.Imaging.ImageFormat.Jpeg);
                }

                // 删除临时文件
                try
                {
                    File.Delete(tempFilePath);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "删除临时文件失败：{Path}", tempFilePath);
                }

                // 保存图片路径到包裹对象
                package.ImagePath = filePath;

                Log.Debug("图像已保存：{FilePath}", filePath);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "保存图像时发生错误");
        }
    }

    private void UpdatePackageStatus(string status)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var items = PackageInfoItems.ToList();
            items[3].Value = status; // 更新状态项
            items[2].Value = DateTime.Now.ToString("HH:mm:ss"); // 更新时间项
            PackageInfoItems = [.. items];
        });
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
            if (SetProperty(ref _volumeImage, value)) Log.Debug("VolumeImage属性已更新");
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
    ///     打开历史记录窗口命令
    /// </summary>
    public ICommand OpenHistoryWindowCommand { get; }

    public ObservableCollection<SelectablePalletModel> AvailablePallets
    {
        get => _availablePallets;
        set => SetProperty(ref _availablePallets, value);
    }

    private SelectablePalletModel? SelectedPallet
    {
        get => _selectedPallet;
        set => SetProperty(ref _selectedPallet, value);
    }

    public DelegateCommand<SelectablePalletModel> SelectPalletCommand { get; }

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
            new PackageInfoItem("Size", "0 × 0 × 0", "cm", "Length × Width × Height", "Ruler24"),
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

    private void ExecuteSelectPallet(SelectablePalletModel pallet)
    {
        foreach (var availablePallet in AvailablePallets) availablePallet.IsSelected = availablePallet == pallet;

        SelectedPallet = pallet;
    }

    private void LoadAvailablePallets()
    {
        try
        {
            var palletSettings = _settingsService.LoadSettings<PalletSettings>();

            AvailablePallets.Clear();

            // 添加空托盘选项
            var emptyPallet = new SelectablePalletModel(new PalletModel
            {
                Name = "noPallet",
                Weight = 0,
                Length = 0,
                Width = 0,
                Height = 0
            });
            AvailablePallets.Add(emptyPallet);

            // 添加配置的托盘
            foreach (var pallet in palletSettings.Pallets) AvailablePallets.Add(new SelectablePalletModel(pallet));

            // 默认选择空托盘
            emptyPallet.IsSelected = true;
            SelectedPallet = emptyPallet;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载托盘配置失败");
        }
    }

    #endregion
}
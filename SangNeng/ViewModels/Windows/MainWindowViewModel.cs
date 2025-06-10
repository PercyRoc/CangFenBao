using Common.Data;
using Common.Models.Package;
using Common.Services.Audio;
using Common.Services.Settings;
using Common.Services.Ui;
using DeviceService.DataSourceDevices.Camera;
// using DeviceService.DataSourceDevices.Camera.Hikvision;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using DeviceService.DataSourceDevices.Camera.Models.Camera.Enums;
using DeviceService.DataSourceDevices.Camera.RenJia;
using DeviceService.DataSourceDevices.Scanner;
using DeviceService.DataSourceDevices.Weight;
using Serilog;
using SharedUI.Models;
using Sunnen.Events;
using Sunnen.Models;
using Sunnen.Services;
using Sunnen.ViewModels.Settings;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Drawing.Color;
using Timer = System.Timers.Timer;

namespace Sunnen.ViewModels.Windows;

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
    private readonly ISangNengService _sangNengService;
    private readonly ISettingsService _settingsService;
    private readonly Timer _timer;
    private readonly RenJiaCameraService _volumeCamera;
    private readonly SerialPortWeightService _weightService;
    private readonly IDisposable? _barcodeSubscription;
    private readonly INotificationService _notificationService;
    private ObservableCollection<SelectablePalletModel> _availablePallets;
    private string _currentBarcode = string.Empty;
    private ImageSource? _currentImage;
    private PackageInfo? _currentPackage;
    private ObservableCollection<DeviceStatus> _deviceStatuses = [];
    private bool _disposed;
    private bool _hasPlayedErrorSound;
    private ObservableCollection<PackageInfo> _packageHistory = [];
    private ObservableCollection<PackageInfoItem> _packageInfoItems = [];
    private SelectablePalletModel? _selectedPallet;
    private ObservableCollection<StatisticsItem> _statisticsItems = [];
    private SystemStatus _systemStatus = SystemStatus.GetCurrentStatus();
    private ImageSource? _volumeImage;
    private string _lastProcessedBarcode = string.Empty;
    private DateTime _lastProcessedTime = DateTime.MinValue;
    private const int DuplicateBarcodeIntervalMs = 500;
    private readonly SemaphoreSlim _barcodeProcessingLock = new(1, 1);

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
        IEventAggregator eventAggregator,
        ISangNengService sangNengService,
        INotificationService notificationService)
    {
        _dialogService = dialogService;
        _volumeCamera = volumeCamera;
        _cameraService = cameraService;
        _weightService = weightService;
        _settingsService = settingsService;
        _packageDataService = packageDataService;
        _audioService = audioService;
        _sangNengService = sangNengService;
        _notificationService = notificationService;

        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        OpenHistoryWindowCommand = new DelegateCommand(() => { _dialogService.ShowDialog("HistoryWindow"); });
        InitializePackageInfoItems();
        InitializeStatisticsItems();
        InitializeDeviceStatuses();

        // 订阅扫码流
        _barcodeSubscription = scannerService.BarcodeStream
            .Subscribe(
                barcode => _ = ProcessBarcodeAsync(barcode), // 使用弃元 `_` 显式忽略 Task，抑制 CS4014 警告
                ex => Log.Error(ex, "扫码流发生错误") // 处理流中的错误
            );

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
            .Subscribe(imageData =>
            {
                try
                {
                    UpdateImageDisplay(imageData, bitmap =>
                    {
                        VolumeImage = bitmap;
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理体积相机图像流数据时发生错误");
                }
            });
        _cameraService.ImageStream
            .Subscribe(imageData =>
            {
                try
                {
                    UpdateImageDisplay(imageData, bitmap =>
                    {
                        CurrentImage = bitmap;
                    });
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
        UpdateInitialDeviceStatuses();
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
            _barcodeSubscription?.Dispose();
            _volumeCamera.ConnectionChanged -= OnVolumeCameraConnectionChanged;
            _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
            _weightService.ConnectionChanged -= OnWeightScaleConnectionChanged;

            // 释放信号量
            _measurementLock.Dispose();
            _barcodeProcessingLock.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放资源时发生错误");
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static void UpdateImageDisplay(BitmapSource image, Action<BitmapSource> imageUpdater)
    {
        try
        {
            // 创建仅在需要更新时才会执行的延迟操作
            var updateOperation = new Action(() =>
            {
                try
                {
                    // 移除图像缩放逻辑
                    var width = image.Width;
                    var height = image.Height;

                    var stride = (int)(width * 4); // 假设为 Bgra32
                    var pixelData = new byte[stride * (int)height];
                    
                    // 不需要缩放，直接复制
                    image.CopyPixels(new Int32Rect(0, 0, (int)width, (int)height), pixelData, stride, 0);
                    

                    // 在UI线程创建WriteableBitmap (使用原始尺寸)
                    var bitmap = new WriteableBitmap((int)width, (int)height, 96, 96, PixelFormats.Bgra32, null);
                    bitmap.WritePixels(new Int32Rect(0, 0, (int)width, (int)height), pixelData, stride, 0);
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
            var successCount = PackageHistory.Count(p => p.Status == PackageStatus.Success);
            items[1].Value = successCount.ToString();

            // 更新失败数
            var failedCount = PackageHistory.Count(p => p.Status == PackageStatus.Failed);
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

    // 将原有的实现移到这个方法中，并设为 internal 以便 View 调用
    internal async Task ProcessBarcodeAsync(string barcode)
    {
        // 尝试获取处理锁，如果已被占用则直接返回
        if (!await _barcodeProcessingLock.WaitAsync(0)) // 设置超时为0，如果锁不可用则立即返回false
        {
            Log.Warning("处理单元繁忙，忽略条码: {Barcode}", barcode);
            return;
        }

        // Use a temporary variable for the barcode to ensure consistency
        var currentBarcodeToProcess = barcode;

        // Schedule UI update on the UI thread immediately, without internal delays
        // We don't necessarily need to wait for this UI update to finish before starting the main delay
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // Clear previous info display items
            InitializePackageInfoItems();
            // Directly set the new barcode for UI display
            CurrentBarcode = currentBarcodeToProcess;
            Log.Information("UI已更新条码: {Barcode}", currentBarcodeToProcess);
            // Update time display immediately within the same dispatch
            var items = PackageInfoItems.ToList();
            if (items.Count <= 2) return; // Ensure items are initialized and index is valid
            items[2].Value = DateTime.Now.ToString("HH:mm:ss"); // Update time display
            PackageInfoItems = [.. items]; // Update the collection
        });

        try // 单个 Try 块，管理信号量释放
        {
            // --- 原始逻辑开始 ---
            // 加载体积相机设置
            var volumeSettings = _settingsService.LoadSettings<VolumeSettings>();
            Log.Information("等待处理包裹，延时: {TimeoutMs}毫秒", volumeSettings.TimeoutMs);

            // 等待配置的时间 - This delay happens *after* the UI update has been scheduled
            await Task.Delay(volumeSettings.TimeoutMs);

            Log.Information("延时结束，开始处理包裹: {Barcode}", currentBarcodeToProcess); // Log with the correct barcode for this instance

            try // 内部 Try 块，处理包裹逻辑和捕获具体异常
            {
                // 防止短时间内重复处理相似条码 (检查包含关系)
                var now = DateTime.Now;
                var isDuplicate = false;
                if (!string.IsNullOrEmpty(_lastProcessedBarcode) &&
                    (now - _lastProcessedTime).TotalMilliseconds < DuplicateBarcodeIntervalMs)
                {
                    if ((_lastProcessedBarcode.Contains(currentBarcodeToProcess) && currentBarcodeToProcess.Length > 5) ||
                        (currentBarcodeToProcess.Contains(_lastProcessedBarcode) && _lastProcessedBarcode.Length > 5))
                    {
                        isDuplicate = true;
                    }
                }

                if (isDuplicate)
                {
                    Log.Warning("忽略相似条码：{Barcode}，上一条码：{LastBarcode}，间隔仅 {Interval} 毫秒",
                        currentBarcodeToProcess, _lastProcessedBarcode, (now - _lastProcessedTime).TotalMilliseconds);
                    return; // 注意：这里直接返回，会执行外层 finally 释放锁
                }

                // 更新最后处理的条码和时间
                _lastProcessedBarcode = currentBarcodeToProcess;
                _lastProcessedTime = now;

                // 重置错误音效播放标志
                _hasPlayedErrorSound = false;

                Log.Information("开始创建包裹对象: {Barcode}", currentBarcodeToProcess); // Use consistent barcode

                // 创建新的包裹对象
                _currentPackage = PackageInfo.Create();
                _currentPackage.SetBarcode(currentBarcodeToProcess); // Use consistent barcode
                _currentPackage.SetStatus(PackageStatus.Created);

                // 设置当前选中的托盘信息到包裹实例
                if (SelectedPallet != null)
                {
                    _currentPackage.SetPallet(
                        SelectedPallet.Name,
                        SelectedPallet.Weight,
                        SelectedPallet.Length,
                        SelectedPallet.Width,
                        SelectedPallet.Height);

                    Log.Information("设置包裹托盘信息：{PalletName}, 重量: {Weight}kg, 尺寸: {Length}×{Width}×{Height}cm",
                        SelectedPallet.Name, SelectedPallet.Weight,
                        SelectedPallet.Length, SelectedPallet.Width, SelectedPallet.Height);
                }
                var cts = new CancellationTokenSource();
                try
                {
                    // 创建三个并行任务
                    Task<bool> photoTask;
                    BitmapSource? capturedImage = null;
                    var imageLock = new object();

                    if (_cameraService is HikvisionIndustrialCameraService hikvisionCamera)
                    {
                        // 订阅图像流，等待一帧图像
                        var imageTask = Task.Run(async () =>
                        {
                            var tcs = new TaskCompletionSource<BitmapSource?>();

                            using var imageSubscription = hikvisionCamera.ImageStream
                                .Take(1) // 只取一帧
                                .Timeout(TimeSpan.FromSeconds(5)) // 5秒超时
                                .Subscribe(
                                    onNext: imageData => tcs.TrySetResult(imageData), // Signal completion with image data
                                    onError: ex => tcs.TrySetException(ex),           // Signal error
                                    onCompleted: () => tcs.TrySetResult(null)         // Signal completion if stream ends before Take(1)
                                );

                            try
                            {
                                // Wait for the image or timeout/cancellation
                                await using (cts.Token.Register(() => tcs.TrySetCanceled()))
                                {
                                    var receivedImageData = await tcs.Task; // Wait for image data from subscribe

                                    if (receivedImageData != null)
                                    {
                                        BitmapSource? frozenClone = null;
                                        // Switch to UI thread to clone and freeze
                                        await Application.Current.Dispatcher.InvokeAsync(() =>
                                        {
                                            try
                                            {
                                                var clone = receivedImageData.Clone();
                                                clone.Freeze(); // Freeze makes it thread-safe
                                                frozenClone = clone;
                                                CurrentImage = clone; // Update UI on UI thread
                                                Log.Information("已在UI线程克隆并冻结图像");
                                            }
                                            catch (Exception uiEx)
                                            {
                                                Log.Error(uiEx, "在UI线程克隆/冻结图像时出错");
                                            }
                                        });

                                        if (frozenClone != null)
                                        {
                                            lock (imageLock)
                                            {
                                                capturedImage = frozenClone; // Store the frozen clone
                                            }

                                            Log.Information("已从图像流获取并处理一帧图像");
                                            return true; // Success
                                        }
                                    }

                                    Log.Warning("未能从图像流获取有效图像数据");
                                    return false; // Failed to get image data
                                }
                            }
                            catch (TimeoutException)
                            {
                                Log.Warning("获取图像流超时");
                                return false;
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "从图像流获取图像失败");
                                return false;
                            }
                        }, cts.Token); // Pass token to Task.Run

                        photoTask = imageTask;
                    }
                    else
                    {
                        photoTask = Task.FromResult(false);
                    }

                    // 使用 cts.Token，虽然没有超时，但保留以便将来可能的其他取消逻辑
                    var volumeTask = TriggerVolumeCamera(cts.Token);

                    // 同样为重量任务复制Token
                    var weightCancellationToken = cts.Token;
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
                                _currentPackage.SetWeight(actualWeight);
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
                    }, weightCancellationToken); // 使用复制的Token

                    // 等待所有任务完成
                    await Task.WhenAll(photoTask, volumeTask, weightTask);

                    // --- BEGIN: Save Dimension Images Logic ---
                    try
                    {
                        if (_currentPackage != null && volumeSettings.ImageSaveMode != DimensionImageSaveMode.None && _currentPackage.Length > 0)
                        {
                            Log.Information("根据配置获取尺寸刻度图，模式: {Mode}", volumeSettings.ImageSaveMode);
                            var dimensionImagesResult = await _volumeCamera.GetDimensionImagesAsync();

                            if (dimensionImagesResult.IsSuccess)
                            {
                                var savePath = volumeSettings.ImageSavePath;
                                var dateFolder = DateTime.Now.ToString("yyyy-MM-dd");
                                var fullDirectoryPath = Path.Combine(savePath, dateFolder);
                                Directory.CreateDirectory(fullDirectoryPath); // Ensure directory exists

                                var sanitizedBarcode = string.Join("_", _currentPackage.Barcode.Split(Path.GetInvalidFileNameChars()));
                                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                                ImageFormat saveFormat = ImageFormat.Jpeg; // Or read from settings if needed

                                // Save Vertical View
                                if ((volumeSettings.ImageSaveMode == DimensionImageSaveMode.Vertical || volumeSettings.ImageSaveMode == DimensionImageSaveMode.Both) && dimensionImagesResult.VerticalViewImage != null)
                                {
                                    var fileName = $"{sanitizedBarcode}_{timestamp}_Vertical.jpg"; // Assuming Jpeg
                                    var filePath = Path.Combine(fullDirectoryPath, fileName);
                                    await SaveDimensionImageAsync(dimensionImagesResult.VerticalViewImage, filePath, saveFormat);
                                    Log.Information("已保存俯视图: {FilePath}", filePath);
                                }

                                // Save Side View
                                if ((volumeSettings.ImageSaveMode == DimensionImageSaveMode.Side || volumeSettings.ImageSaveMode == DimensionImageSaveMode.Both) && dimensionImagesResult.SideViewImage != null)
                                {
                                    var fileName = $"{sanitizedBarcode}_{timestamp}_Side.jpg"; // Assuming Jpeg
                                    var filePath = Path.Combine(fullDirectoryPath, fileName);
                                    await SaveDimensionImageAsync(dimensionImagesResult.SideViewImage, filePath, saveFormat);
                                     Log.Information("已保存侧视图: {FilePath}", filePath);
                                }
                                Log.Debug("已释放尺寸刻度图 BitmapSource 引用");
                            }
                            else
                            {
                                Log.Warning("获取尺寸刻度图失败: {Error}", dimensionImagesResult.ErrorMessage);
                            }
                        }
                        else if (_currentPackage?.Length <= 0)
                        {
                             Log.Debug("体积测量未成功，跳过获取尺寸刻度图");
                        }
                    }
                    catch(Exception imgEx)
                    {
                        Log.Error(imgEx, "获取或保存尺寸刻度图时发生错误");
                    }
                    // --- END: Save Dimension Images Logic ---

                    // 更新体积（使用cm³作为单位）
                    _currentPackage!.SetDimensions(_currentPackage.Length ?? 0, _currentPackage.Width ?? 0,
                        _currentPackage.Height ?? 0);

                    // 检查三个必要条件是否都满足：条码、重量和体积
                    var isBarcodeMissing = string.IsNullOrEmpty(_currentPackage.Barcode);
                    var isWeightMissing = _currentPackage.Weight <= 0;
                    var isVolumeMissing = (_currentPackage.Length ?? 0) <= 0 ||
                                          (_currentPackage.Width ?? 0) <= 0 ||
                                          (_currentPackage.Height ?? 0) <= 0;

                    var isComplete = !isBarcodeMissing && !isWeightMissing && !isVolumeMissing;
                    var completionMessage = "Complete"; // 成功时的默认消息

                    if (!isComplete)
                    {
                        Log.Warning("包裹信息不完整。条码缺失: {BarcodeMissing}, 重量缺失: {WeightMissing}, 体积缺失: {VolumeMissing}",
                            isBarcodeMissing, isWeightMissing, isVolumeMissing);

                        // 确定具体的错误消息
                        if (isBarcodeMissing) completionMessage = "Missing Barcode";
                        else if (isWeightMissing) completionMessage = "Missing Weight";
                        else if (isVolumeMissing) completionMessage = "Missing Volume";
                        else completionMessage = "Data Incomplete"; // 后备，理论上不会发生

                        _currentPackage.SetStatus(PackageStatus.Failed, completionMessage);
                        PlayErrorSound();
                        // 移除立即更新UI的调用，保留音效播放
                    }
                    else
                    {
                        // 仅在信息完整时播放成功音效
                        _ = _audioService.PlayPresetAsync(AudioType.Success);
                        _currentPackage.SetStatus(PackageStatus.Success, completionMessage); // 使用默认的 "Complete" 消息
                        // 移除立即更新UI的调用，保留音效播放
                    }

                    // 保存捕获的图像（总是执行，如果已捕获，即使包裹不完整也可能保存）
                    BitmapSource? imageToProcess = null;
                    string? base64Image = null;
                    string? imageName = null;

                    try
                    {
                        lock (imageLock)
                        {
                            if (capturedImage != null)
                            {
                                // No need to clone here, capturedImage is already a frozen clone
                                imageToProcess = capturedImage;
                                capturedImage = null; // Clear the reference if needed (optional)
                            }
                        }

                        if (imageToProcess != null) // imageToProcess is the frozen image
                            try
                            {
                                var cameraSettings = _settingsService.LoadSettings<CameraSettings>("CameraSettings");
                                // 使用新的CancellationTokenSource，不设置超时
                                using var imageSaveCts = new CancellationTokenSource();
                                // Pass the frozen image directly to SaveImageAsync
                                var result = await SaveImageAsync(imageToProcess, cameraSettings, _currentPackage, // Pass _currentPackage which has the correct barcode
                                    imageSaveCts.Token);
                                if (result.HasValue) (base64Image, imageName) = result.Value;
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "保存图像到文件时发生错误");
                                // 不在此处更新包状态，因为它可能已经是Failed或Error
                            }
                        else
                            Log.Warning("未能捕获到图像");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "处理图像时发生错误: {Error}", ex.Message);
                        // 不在此处更新包状态
                    }

                    // 调用桑能接口上传数据 (仅当信息完整时)
                    if (isComplete) // 检查完整性标志
                    {
                        try
                        {
                            var request = new SangNengWeightRequest
                            {
                                Barcode = _currentPackage.Barcode, // Use barcode from the package object
                                Weight = _currentPackage.Weight,
                                Length = _currentPackage.Length ?? 0,
                                Width = _currentPackage.Width ?? 0,
                                Height = _currentPackage.Height ?? 0,
                                Volume = _currentPackage.Volume ?? 0,
                                Timestamp = _currentPackage.CreateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                                Image = base64Image ?? string.Empty,
                                ImageName = imageName ?? string.Empty
                            };

                            var response = await _sangNengService.SendWeightDataAsync(request);
                            if (response.Code != 1) // 修改为判断 Code == 1 表示成功
                            {
                                Log.Warning("上传数据到桑能服务器失败: {Message}", response.Message);
                                // 如果上传失败，保持状态为 Success 但记录错误
                                // Application.Current.Dispatcher.Invoke(() => UpdatePackageStatusWithEnum(PackageStatus.Error, "Upload Failed"));
                            }
                            else
                            {
                                Log.Information("成功上传数据到桑能服务器");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "上传数据到桑能服务器时发生错误");
                            // 记录错误，但不改变 Success 状态
                            // Application.Current.Dispatcher.Invoke(() => UpdatePackageStatusWithEnum(PackageStatus.Error, "Upload Error"));
                        }
                    }
                    else
                    {
                        Log.Information("包裹信息不完整，跳过上传到桑能服务器");
                    }


                    // 在后台保存到数据库（总是执行）
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // 移除此处的完整性检查
                            await _packageDataService.AddPackageAsync(_currentPackage);
                            Log.Information("包裹数据已保存到数据库：{Barcode}, 状态: {Status}, 消息: {StatusDisplay}",
                                _currentPackage.Barcode, _currentPackage.Status, _currentPackage.StatusDisplay); // Use barcode from the package object
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "保存包裹数据到数据库时发生错误：{Barcode}", _currentPackage.Barcode); // Use barcode from the package object
                            // 如果数据库保存失败，只记录日志
                            // Application.Current.Dispatcher.Invoke(() => UpdatePackageStatusWithEnum(PackageStatus.Error, "Database Error"));
                        }
                        // 使用 CancellationToken.None 以确保即使主操作超时也尝试保存
                    }, CancellationToken.None);
                }
                catch (Exception ex) // 捕获包裹处理过程中的其他通用异常
                {
                    Log.Error(ex, "处理扫码信息时发生未预期的错误: {Barcode}", currentBarcodeToProcess); // Use consistent barcode
                    _currentPackage?.SetStatus(PackageStatus.Error, "Processing Error");
                    PlayErrorSound();
                }
            }
            catch (Exception ex) // 捕获包裹处理过程中的其他通用异常
            {
                Log.Error(ex, "处理扫码信息时发生未预期的错误: {Barcode}", currentBarcodeToProcess); // Use consistent barcode

                _currentPackage?.SetStatus(PackageStatus.Error, "Processing Error");
                PlayErrorSound();
            }
            // --- 原始逻辑结束 ---

            // 最后一次性更新所有UI：实时区域状态和历史记录（移到方法末尾）
            if (_currentPackage != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    // 1. 先更新实时区域显示
                    UpdateCurrentPackageUiDisplay(_currentPackage.Status, _currentPackage.StatusDisplay);

                    // 2. 然后更新历史记录
                    PackageHistory.Insert(0, _currentPackage);

                    // 3. 最后更新统计信息
                    UpdateStatistics();
                });
            }
        }
        finally // 关联外层 Try，确保释放信号量
        {
            _barcodeProcessingLock.Release(); // 确保锁在方法结束时被释放
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
                _currentPackage.SetStatus(PackageStatus.Created, "Measuring");

                // 触发测量
                var result = await Task.Run(_volumeCamera.TriggerMeasure, cancellationToken);
                if (!result.IsSuccess)
                {
                    Log.Error("体积测量失败：{Error}", result.ErrorMessage);
                    _currentPackage.SetDimensions(0, 0, 0);
                    _currentPackage.SetStatus(PackageStatus.Failed, result.ErrorMessage);
                    PlayErrorSound();
                    return;
                }

                // 更新测量结果（将毫米转换为厘米）
                _currentPackage.SetDimensions(result.Length / 10.0, result.Width / 10.0, result.Height / 10.0);
                _currentPackage.SetStatus(PackageStatus.Success, "Success");
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
                _currentPackage.SetStatus(PackageStatus.Error, ex.Message);
                PlayErrorSound();
            }
        }
    }

    /// <summary>
    /// 保存图像到文件，添加水印，并返回Base64编码的图像和名称
    /// </summary>
    private static async Task<(string base64Image, string imageName)?> SaveImageAsync(BitmapSource image,
        CameraSettings settings, PackageInfo package,
        CancellationToken cancellationToken)
    {
        string? tempFilePath = null;
        try
        {
            string? base64Image = null;
            var imageName = string.Empty;

            await Task.Run(() =>
            {
                try
                {
                    // 确保保存目录存在
                    if (!Directory.Exists(settings.ImageSavePath)) Directory.CreateDirectory(settings.ImageSavePath);

                    // 清理条码中的非法文件名字符
                    var sanitizedBarcode = string.Join("_", package.Barcode.Split(Path.GetInvalidFileNameChars()));

                    // 生成文件名（使用清理后的条码和时间戳）
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                    imageName = $"{sanitizedBarcode}_{timestamp}"; // 使用清理后的条码
                    var extension = settings.ImageFormat switch
                    {
                        ImageFormat.Jpeg => ".jpg",
                        ImageFormat.Png => ".png",
                        ImageFormat.Bmp => ".bmp",
                        ImageFormat.Tiff => ".tiff",
                        _ => ".jpg"
                    };
                    var filePath = Path.Combine(settings.ImageSavePath, imageName + extension);

                    // 先保存BitmapSource到临时文件
                    tempFilePath = Path.Combine(settings.ImageSavePath, $"temp_{imageName}{extension}");

                    // 使用BitmapEncoder保存BitmapSource
                    BitmapEncoder encoder = settings.ImageFormat switch
                    {
                        ImageFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = 90 },
                        ImageFormat.Png => new PngBitmapEncoder(),
                        ImageFormat.Bmp => new BmpBitmapEncoder(),
                        ImageFormat.Tiff => new TiffBitmapEncoder(),
                        _ => new JpegBitmapEncoder { QualityLevel = 90 }
                    };

                    encoder.Frames.Add(BitmapFrame.Create(image));

                    using (var fileStream =
                           new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        encoder.Save(fileStream);
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        Log.Warning("保存图像操作被取消");
                        return Task.CompletedTask;
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
                            $"Barcode: {package.Barcode}", // 水印中仍显示原始条码
                            $"Size: {package.Length:F1}cm × {package.Width:F1}cm × {package.Height:F1}cm",
                            $"Weight: {package.Weight:F3}kg",
                            $"Volume: {package.Volume / 1000.0:N0}cm³",
                            $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
                        };

                        // 如果有使用托盘，添加托盘信息到水印
                        if (!string.IsNullOrEmpty(package.PalletName) && package.PalletName != "noPallet")
                        {
                            var palletLines = new List<string>(watermarkLines);
                            palletLines.Insert(1, $"Pallet: {package.PalletName} ({package.PalletWeight:F3}kg)");
                            watermarkLines = [.. palletLines];
                        }

                        const int padding = 20;
                        const int lineSpacing = 50;
                        var startY = padding;

                        foreach (var line in watermarkLines)
                        {
                            graphics.DrawString(line, font, brush, padding, startY);
                            startY += lineSpacing;
                        }

                        // 保存带水印的图像
                        bitmap.Save(filePath, System.Drawing.Imaging.ImageFormat.Jpeg);

                        // 转换为Base64
                        using var ms = new MemoryStream();
                        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                        base64Image = Convert.ToBase64String(ms.ToArray());
                    }

                    // 保存图片路径到包裹对象
                    package.ImagePath = filePath;

                    Log.Debug("图像已保存：{FilePath}", filePath);
                }
                catch (OperationCanceledException)
                {
                    Log.Warning("图像处理操作被取消");
                    throw; // 重新抛出异常，让上层处理
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "图像处理过程中发生错误: {Error}", ex.Message);
                    throw;
                }

                return Task.CompletedTask;
            }, cancellationToken);

            return (base64Image!, imageName);
        }
        catch (OperationCanceledException)
        {
            Log.Warning("保存图像任务被取消");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存图像时发生错误");
            return null;
        }
        finally
        {
            // 清理临时文件
            if (tempFilePath != null && File.Exists(tempFilePath))
                try
                {
                    File.Delete(tempFilePath);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "删除临时文件失败：{Path}", tempFilePath);
                }
        }
    }

    /// <summary>
    /// 更新当前包裹处理结果的UI显示（重量、尺寸、时间、状态）
    /// </summary>
    private void UpdateCurrentPackageUiDisplay(PackageStatus status, string customDisplay)
    {
        if (_currentPackage == null) return; // Safety check

        var items = PackageInfoItems.ToList();

        // Update Weight
        items[0].Value = $"{_currentPackage.Weight:F3}";
        // Update Size
        items[1].Value = $"{_currentPackage.Length:F1}x{_currentPackage.Width:F1}x{_currentPackage.Height:F1}";
        // Update Time
        items[2].Value = DateTime.Now.ToString("HH:mm:ss");
        // Update Status
        var statusItem = items[3];
        string displayText;
        string color;

        switch (status)
        {
            case PackageStatus.Success:
                displayText = "Success"; // Using standard "Success" for display consistency
                color = "#4CAF50"; // Green
                break;
            case PackageStatus.Failed:
                // Use custom display if available (e.g., "Missing Weight"), otherwise "Failed"
                displayText = !string.IsNullOrEmpty(customDisplay) && customDisplay != "Complete" ? customDisplay : "Failed";
                color = "#FF0000"; // Red
                break;
            case PackageStatus.Error:
                displayText = !string.IsNullOrEmpty(customDisplay) ? customDisplay : "Error";
                color = "#FF0000"; // Red
                break;
            default: // Should not happen here, but handle defensively
                displayText = "Waiting";
                color = "#808080"; // Gray
                break;
        }
        statusItem.Value = displayText;
        statusItem.StatusColor = color;

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

    private void OnCameraConnectionChanged(string? deviceId, bool isConnected)
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
        set => SetProperty(ref _currentBarcode, value);
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
            new PackageInfoItem("Weight", "0.00", "kg", "Package Weight", "Scales24"),
            new PackageInfoItem("Size", "0 × 0 × 0", "cm", "Length × Width × Height", "Ruler24"),
            new PackageInfoItem("Time", "--:--:--", "", "Processing Time", "Timer24"),
            new PackageInfoItem("Status", "Waiting", "", "Processing Status", "Alert24")
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
            new DeviceStatus { Name = "Weight Scale", Status = "Offline", Icon = "Scales24", StatusColor = "#FFA500" }
        ];
    }

    private void ExecuteSelectPallet(SelectablePalletModel pallet)
    {
        foreach (var availablePallet in AvailablePallets) availablePallet.IsSelected = availablePallet == pallet;

        SelectedPallet = pallet;

        try
        {
            // 设置托盘高度给人加相机SDK
            var palletHeightMm = (int)(pallet.Height * 10);
            var result = _volumeCamera.SetPalletHeight(palletHeightMm);
            if (result != 0)
            {
                Log.Warning("设置托盘高度失败：{Result}", result);
                _notificationService.ShowWarning($"Failed to set pallet height for '{pallet.Name}' (Error code: {result}). Please try selecting the pallet again.");
            }
            else
            {
                Log.Information("成功设置托盘高度：{Height}mm", palletHeightMm);
                _notificationService.ShowSuccess($"Pallet '{pallet.Name}' with height {pallet.Height:F1}cm selected successfully");
            }

            // 保存选择的托盘名称到配置
            var mainWindowSettings = _settingsService.LoadSettings<MainWindowSettings>();
            mainWindowSettings.LastSelectedPalletName = pallet.Name;
            _settingsService.SaveSettings(mainWindowSettings);
            Log.Information("已保存用户选择的托盘：{PalletName}", pallet.Name);

        }
        catch (Exception ex)
        {
            Log.Error(ex, "选择或设置托盘高度时发生错误");
            _notificationService.ShowError($"Error selecting/setting pallet height: {ex.Message}. Please try selecting the pallet again.");
        }
    }

    private void LoadAvailablePallets()
    {
        try
        {
            var palletSettings = _settingsService.LoadSettings<PalletSettings>();
            var mainWindowSettings = _settingsService.LoadSettings<MainWindowSettings>();
            var lastSelectedPalletName = mainWindowSettings.LastSelectedPalletName ?? "noPallet"; // 确保有默认值

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
            foreach (var pallet in palletSettings.Pallets)
            {
                AvailablePallets.Add(new SelectablePalletModel(pallet));
            }

            // 查找并选中上次选择的托盘
            var palletToSelect = AvailablePallets.FirstOrDefault(p => p.Name == lastSelectedPalletName);

            if (palletToSelect != null)
            {
                palletToSelect.IsSelected = true;
                SelectedPallet = palletToSelect;
                Log.Information("已加载并选中上次保存的托盘：{PalletName}", palletToSelect.Name);
                // 重新触发一次相机设置，确保相机使用保存的托盘高度
                if (palletToSelect.Name != "noPallet")
                {
                    try
                    {
                        var palletHeightMm = (int)(palletToSelect.Height * 10);
                        var result = _volumeCamera.SetPalletHeight(palletHeightMm);
                        if (result != 0)
                        {
                            Log.Warning("启动时设置上次托盘高度失败：{Result}", result);
                            // 这里可以选择是否通知用户，或者只记录日志
                        }
                        else
                        {
                            Log.Information("启动时成功设置上次托盘高度：{Height}mm", palletHeightMm);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "启动时设置上次托盘高度时发生错误");
                    }
                }
            }
            else
            {
                // 如果找不到上次保存的托盘（可能已被删除），则默认选择空托盘
                emptyPallet.IsSelected = true;
                SelectedPallet = emptyPallet;
                Log.Warning("上次保存的托盘 '{LastPalletName}' 未找到，已默认选择空托盘", lastSelectedPalletName);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载托盘配置或应用上次选择时失败");
            // 出现异常时，确保默认选择空托盘
            var emptyPallet = AvailablePallets.FirstOrDefault(p => p.Name == "noPallet");
            if (emptyPallet != null)
            {
                emptyPallet.IsSelected = true;
                SelectedPallet = emptyPallet;
            }
        }
    }

    // --- 新增：检查并更新初始设备状态的方法 ---
    private void UpdateInitialDeviceStatuses()
    {
        Log.Debug("检查并更新初始设备状态...");
        try
        {
            // 更新拍照相机状态
            OnCameraConnectionChanged(_cameraService.GetType().Name, _cameraService.IsConnected);

            // 更新体积相机状态
            OnVolumeCameraConnectionChanged(_volumeCamera.GetType().Name, _volumeCamera.IsConnected);

            // 更新重量称状态 (IsConnected 现在是 public)
            OnWeightScaleConnectionChanged(_weightService.GetType().Name, _weightService.IsConnected);

            Log.Debug("初始设备状态更新完成。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新初始设备状态时发生错误");
        }
    }
    // --- 新增结束 ---

    /// <summary>
    /// Asynchronously saves a BitmapSource to a file.
    /// </summary>
    /// <param name="image">The BitmapSource to save.</param>
    /// <param name="filePath">The full path where the image will be saved.</param>
    /// <param name="format">The desired image format (default is Jpeg).</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private static async Task SaveDimensionImageAsync(BitmapSource image, string filePath, ImageFormat format = ImageFormat.Jpeg)
    {
        try
        {
            await Task.Run(() =>
            {
                BitmapEncoder encoder = format switch
                {
                    ImageFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = 90 },
                    ImageFormat.Png => new PngBitmapEncoder(),
                    ImageFormat.Bmp => new BmpBitmapEncoder(),
                    ImageFormat.Tiff => new TiffBitmapEncoder(),
                    _ => new JpegBitmapEncoder { QualityLevel = 90 }
                };

                encoder.Frames.Add(BitmapFrame.Create(image));

                try
                {
                    using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                    encoder.Save(fileStream);
                }
                catch (IOException ioEx)
                {
                    // Log IO specific errors more granularly
                    Log.Error(ioEx, "保存尺寸图像到文件时发生IO错误: {FilePath}", filePath);
                    // Re-throw or handle as needed, here we just log
                }
                 catch (UnauthorizedAccessException uaEx)
                {
                     Log.Error(uaEx, "保存尺寸图像到文件时权限不足: {FilePath}", filePath);
                }
            });
        }
        catch (Exception ex)
        {
            // Catch exceptions from Task.Run or encoder creation
            Log.Error(ex, "保存尺寸图像时发生未预期的错误: {FilePath}", filePath);
        }
    }

    #endregion
}
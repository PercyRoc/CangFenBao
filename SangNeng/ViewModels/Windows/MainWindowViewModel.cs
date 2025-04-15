using Common.Data;
using Common.Models.Package;
using Common.Services.Audio;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Camera.Hikvision;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using DeviceService.DataSourceDevices.Camera.Models.Camera.Enums;
using DeviceService.DataSourceDevices.Camera.RenJia;
using DeviceService.DataSourceDevices.Scanner;
using DeviceService.DataSourceDevices.Weight;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Prism.Services.Dialogs;
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
using System.Reactive.Linq;
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
    private ObservableCollection<PackageInfoItem> _packageInfoItems = [];
    private SelectablePalletModel? _selectedPallet;
    private ObservableCollection<StatisticsItem> _statisticsItems = [];
    private SystemStatus _systemStatus = SystemStatus.GetCurrentStatus();
    private ImageSource? _volumeImage;
    private string _lastProcessedBarcode = string.Empty;
    private DateTime _lastProcessedTime = DateTime.MinValue;
    private const int DuplicateBarcodeIntervalMs = 500;

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
        ISangNengService sangNengService)
    {
        _dialogService = dialogService;
        _scannerService = scannerService;
        _volumeCamera = volumeCamera;
        _cameraService = cameraService;
        _weightService = weightService;
        _settingsService = settingsService;
        _packageDataService = packageDataService;
        _audioService = audioService;
        _sangNengService = sangNengService;

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
                        imageData.Width,
                        imageData.Height);

                    UpdateImageDisplay(imageData, bitmap =>
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
        if (_cameraService is HikvisionIndustrialCameraService hikvisionCamera)
            hikvisionCamera.ImageStream
                .Subscribe(imageData =>
                {
                    try
                    {
                        UpdateImageDisplay(imageData, bitmap => { CurrentImage = bitmap; });
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

    private static void UpdateImageDisplay(BitmapSource image, Action<BitmapSource> imageUpdater)
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
                        var scaleX = maxDisplayWidth / width;
                        var scaleY = maxDisplayHeight / height;
                        var scale = Math.Min(scaleX, scaleY);

                        width = (int)(width * scale);
                        height = (int)(height * scale);
                        needsResize = true;
                    }

                    var stride = (int)(width * 4); // RGBA每像素4字节
                    var pixelData = new byte[stride * (int)height];

                    // 直接复制像素数据
                    if (needsResize)
                    {
                        // 使用SixLabors.ImageSharp进行缩放
                        var resizedImage = new TransformedBitmap(image, new ScaleTransform(
                            width / image.Width,
                            height / image.Height));
                        resizedImage.CopyPixels(new Int32Rect(0, 0, (int)width, (int)height), pixelData, stride, 0);
                    }
                    else
                    {
                        // 不需要缩放，直接复制
                        image.CopyPixels(new Int32Rect(0, 0, (int)width, (int)height), pixelData, stride, 0);
                    }

                    // 在UI线程创建WriteableBitmap
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

    public void OnBarcodeScanned(object? sender, string barcode)
    {
        // 直接在事件处理程序中调用异步方法，并添加异常处理
        ProcessBarcodeAsync(barcode).ContinueWith(task =>
        {
            if (task is not { IsFaulted: true, Exception: not null }) return;
            // 记录任何未处理的异常
            Log.Error(task.Exception, "处理条码时发生未捕获的异常: {Barcode}", barcode);

            // 确保在UI线程更新界面和播放错误音效
            Application.Current.Dispatcher.Invoke(() =>
            {
                PlayErrorSound();
                UpdatePackageStatusWithEnum(PackageStatus.Error, "System Error");
            });
        }, TaskScheduler.Current);
    }

    // 将原有的实现移到这个私有异步方法中
    private async Task ProcessBarcodeAsync(string barcode)
    {
        // 加载体积相机设置
        var volumeSettings = _settingsService.LoadSettings<VolumeSettings>();
        Log.Information("等待处理包裹，延时: {TimeoutMs}毫秒", volumeSettings.TimeoutMs);

        // 等待配置的时间
        await Task.Delay(volumeSettings.TimeoutMs);

        Log.Information("开始处理包裹: {Barcode}", barcode);

        try
        {
            // 防止短时间内重复处理相同或相似条码
            var now = DateTime.Now;
            var isDuplicate = barcode == _lastProcessedBarcode &&
                              (now - _lastProcessedTime).TotalMilliseconds < DuplicateBarcodeIntervalMs;

            // 检查包含关系的条码（处理条码前缀或后缀问题）
            if (!isDuplicate && !string.IsNullOrEmpty(_lastProcessedBarcode) &&
                (now - _lastProcessedTime).TotalMilliseconds < DuplicateBarcodeIntervalMs)
            {
                // 较长的条码包含较短的条码
                if (_lastProcessedBarcode.Contains(barcode) && barcode.Length > 5 ||
                    barcode.Contains(_lastProcessedBarcode) && _lastProcessedBarcode.Length > 5)
                {
                    isDuplicate = true;
                }
            }

            if (isDuplicate)
            {
                Log.Warning("忽略相似条码：{Barcode}，上一条码：{LastBarcode}，间隔仅 {Interval} 毫秒",
                    barcode, _lastProcessedBarcode, (now - _lastProcessedTime).TotalMilliseconds);
                return;
            }

            // 更新最后处理的条码和时间
            _lastProcessedBarcode = barcode;
            _lastProcessedTime = now;

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
            _currentPackage = PackageInfo.Create();
            _currentPackage.SetBarcode(barcode);
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

            // 并行执行相机拍照、体积测量和重量获取，添加超时处理
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // 10秒超时
            try
            {
                // 创建三个并行任务
                Task<bool> photoTask;
                BitmapSource? capturedImage = null;
                var imageCaptured = false;
                var imageLock = new object();

                if (_cameraService is HikvisionIndustrialCameraService hikvisionCamera)
                {
                    // 在任务开始前复制Token，避免闭包捕获可能被释放的cts
                    var cancellationToken = cts.Token;
                    // 订阅图像流，等待一帧图像
                    var imageTask = Task.Run(() =>
                    {
                        try
                        {
                            using var imageSubscription = hikvisionCamera.ImageStream
                                .Take(1) // 只取一帧
                                .Timeout(TimeSpan.FromSeconds(5)) // 5秒超时
                                .Subscribe(imageData =>
                                {
                                    lock (imageLock)
                                    {
                                        if (imageCaptured) return;
                                        capturedImage = imageData.Clone(); // 保存图像副本
                                        CurrentImage = imageData; // 更新UI显示
                                        imageCaptured = true;
                                        Log.Information("已从图像流获取一帧图像");
                                    }
                                });

                            // 使用局部变量cancellationToken而非闭包中的cts.Token
                            while (!imageCaptured && !cancellationToken.IsCancellationRequested)
                            {
                                Thread.Sleep(100);
                            }

                            return imageCaptured;
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "从图像流获取图像失败");
                            return false;
                        }
                    }, cancellationToken); // 使用复制的Token

                    photoTask = imageTask;
                }
                else
                {
                    photoTask = Task.FromResult(false);
                }

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
                }, weightCancellationToken); // 使用复制的Token

                // 等待所有任务完成
                await Task.WhenAll(photoTask, volumeTask, weightTask);
                // 更新体积（使用cm³作为单位）
                _currentPackage.SetDimensions(_currentPackage.Length ?? 0, _currentPackage.Width ?? 0,
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
                    // 立即更新失败状态的UI
                    Application.Current.Dispatcher.Invoke(() =>
                        UpdatePackageStatusWithEnum(PackageStatus.Failed, completionMessage));
                }
                else
                {
                    // 仅在信息完整时播放成功音效
                    _ = _audioService.PlayPresetAsync(AudioType.Success);
                    _currentPackage.SetStatus(PackageStatus.Success, completionMessage); // 使用默认的 "Complete" 消息
                    // 立即更新成功状态的UI
                    Application.Current.Dispatcher.Invoke(() =>
                        UpdatePackageStatusWithEnum(PackageStatus.Success, completionMessage));
                }

                // ---- 公共步骤 ----

                // 更新UI尺寸显示（总是执行）
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var items = PackageInfoItems.ToList();
                    // 更新尺寸项，转换为cm显示
                    items[1].Value =
                        $"{_currentPackage.Length:F1}x{_currentPackage.Width:F1}x{_currentPackage.Height:F1}";
                    PackageInfoItems = [.. items];
                });

                // 添加到历史记录（总是执行）
                Application.Current.Dispatcher.Invoke(() =>
                {
                    PackageHistory.Insert(0, _currentPackage);
                    // 更新统计信息
                    UpdateStatistics();
                });

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
                            // 对于BitmapSource，复制操作可通过.Clone()或创建一个冻结副本
                            imageToProcess = capturedImage.Clone();
                            capturedImage = null; // 释放原始引用
                        }
                    }

                    if (imageToProcess != null)
                        try
                        {
                            var cameraSettings = _settingsService.LoadSettings<CameraSettings>("CameraSettings");
                            // 使用新的CancellationTokenSource，不设置超时
                            using var imageSaveCts = new CancellationTokenSource();
                            var result = await SaveImageAsync(imageToProcess, cameraSettings, _currentPackage,
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
                            Barcode = _currentPackage.Barcode,
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
                            _currentPackage.Barcode, _currentPackage.Status, _currentPackage.StatusDisplay);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "保存包裹数据到数据库时发生错误：{Barcode}", _currentPackage.Barcode);
                        // 如果数据库保存失败，只记录日志
                        // Application.Current.Dispatcher.Invoke(() => UpdatePackageStatusWithEnum(PackageStatus.Error, "Database Error"));
                    }
                    // 使用 CancellationToken.None 以确保即使主操作超时也尝试保存
                }, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                Log.Warning("处理包裹超时：{Barcode}", barcode);
                UpdatePackageStatusWithEnum(PackageStatus.Error, "Timeout");
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

                    // 生成文件名（使用条码和时间戳）
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                    imageName = $"{package.Barcode}_{timestamp}";
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
                            $"Barcode: {package.Barcode}",
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
                            watermarkLines = palletLines.ToArray();
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
    /// 使用PackageStatus枚举直接更新包裹状态和UI
    /// </summary>
    private void UpdatePackageStatusWithEnum(PackageStatus status, string customDisplay)
    {
        var items = PackageInfoItems.ToList();
        var statusItem = items[3]; // 状态项
        items[2].Value = DateTime.Now.ToString("HH:mm:ss"); // 更新时间项

        // 根据枚举设置显示文字和颜色
        string displayText;
        string color;

        switch (status)
        {
            case PackageStatus.Success:
                displayText = "Success";
                color = "#4CAF50"; // 绿色
                break;

            case PackageStatus.Failed:
                displayText = "Failed";
                color = "#FF0000"; // 红色
                break;

            case PackageStatus.Error:
                // 如果有自定义显示文字，优先使用
                displayText = !string.IsNullOrEmpty(customDisplay) ? customDisplay : "Error";
                color = "#FF0000"; // 红色
                break;

            default:
                // 等待或其他状态
                displayText = "Waiting";
                color = "#808080"; // 灰色
                break;
        }

        // 更新状态项显示
        statusItem.Value = displayText;
        statusItem.StatusColor = color;

        // 更新包裹状态
        if (_currentPackage != null && status != _currentPackage.Status)
        {
            _currentPackage.SetStatus(status, displayText);
        }

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
            }
            else
            {
                Log.Information("成功设置托盘高度：{Height}mm", palletHeightMm);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设置托盘高度时发生错误");
        }
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
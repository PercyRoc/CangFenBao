using System.Reactive.Linq;
using System.Reactive.Subjects;
using Common.Models.Package;
using DeviceService.DataSourceDevices.Camera.Models;
using Microsoft.Extensions.ObjectPool;
using Serilog;
using TurboJpegWrapper;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Threading.Channels;
using System.Buffers;
using Serilog.Context;

namespace DeviceService.DataSourceDevices.Camera.HuaRay;

/// <summary>
/// 华睿相机服务实现类
/// </summary>
public class HuaRayCameraService
{
    #region 私有字段

    /// <summary>
    /// JPEG解压缩器对象池
    /// </summary>
    private static readonly DefaultObjectPool<TJDecompressor> DecompressorPool =
        new(new DefaultPooledObjectPolicy<TJDecompressor>(), Environment.ProcessorCount);

    /// <summary>
    /// 图像处理信号量 - 限制 ProcessPackageInfoAsync 内部的图像处理并发
    /// </summary>
    private readonly SemaphoreSlim _imageProcessingSemaphore =
        new(Math.Max(1, Environment.ProcessorCount / 2), Math.Max(1, Environment.ProcessorCount / 2));

    /// <summary>
    /// 图像数据流
    /// </summary>
    private readonly Subject<(BitmapSource bitmapSource, string cameraId)>
        _imageSubject = new();

    /// <summary>
    /// 华睿相机包装器实例
    /// </summary>
    private readonly HuaRayWrapper _huaRayWrapper;

    /// <summary>
    /// 包裹信息流
    /// </summary>
    private readonly Subject<PackageInfo> _packageSubject = new();

    /// <summary>
    /// 资源释放标志
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// 缓存的配置文件路径
    /// </summary>
    private static string? _cachedConfigPath;

    /// <summary>
    /// 配置文件路径存储文件
    /// </summary>
    private static readonly string ConfigPathStorageFile =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "huaray_config_path.txt");

    // Channel for buffering incoming events
    private Channel<HuaRayCodeEventArgs>? _eventChannel;
    private CancellationTokenSource? _cts;
    private Task? _consumerTask;
    private const int ChannelCapacity = 100; // Configurable capacity for the bounded channel

    #endregion

    #region 公共属性

    /// <summary>
    /// 服务启动事件
    /// </summary>
    public event Action? ServiceStarted;

    /// <summary>
    /// 相机连接状态变化事件
    /// </summary>
    public event Action<string, bool>? ConnectionChanged;

    /// <summary>
    /// 相机连接状态
    /// </summary>
    private bool IsConnected { get; set; }

    /// <summary>
    /// 包裹信息流
    /// </summary>
    public IObservable<PackageInfo> PackageStream => _packageSubject.AsObservable();
    

    /// <summary>
    /// 带相机ID的图像信息流
    /// </summary>
    public IObservable<(BitmapSource Image, string CameraId)> ImageStreamWithId => _imageSubject.AsObservable();

    #endregion

    #region 构造函数

    /// <summary>
    /// 构造函数
    /// </summary>
    internal HuaRayCameraService()
    {
        _huaRayWrapper = HuaRayWrapper.Instance;
    }

    #endregion

    #region 公共方法

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopService();
        _imageProcessingSemaphore.Dispose();
        _imageSubject.Dispose();
        _packageSubject.Dispose();
        _cts?.Dispose();
        Log.Information("华睿相机服务已释放资源.");
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 启动相机服务
    /// </summary>
    /// <returns>启动结果</returns>
    public bool Start()
    {
        return StartService();
    }

    /// <summary>
    /// 停止相机服务
    /// </summary>
    /// <returns>停止结果</returns>
    public bool Stop()
    {
        StopService();
        return true;
    }

    /// <summary>
    /// 获取工作相机列表
    /// </summary>
    /// <returns>相机信息列表</returns>
    public List<HuaRayApiStruct.CameraInfo> GetCameras()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(HuaRayCameraService));
        return _huaRayWrapper.GetWorkCameraInfo();
    }

    /// <summary>
    /// 获取所有可用的相机基本信息
    /// </summary>
    /// <returns>相机基本信息列表</returns>
    public IEnumerable<CameraBasicInfo> GetAvailableCameras()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(HuaRayCameraService));
        var huaRayCameras = _huaRayWrapper.GetWorkCameraInfo();

        return huaRayCameras.Select((camera, index) =>
        {
            // 构造与事件/ViewModel中使用的相机ID格式一致的ID
            var constructedCameraId = (!string.IsNullOrWhiteSpace(camera.camDevVendor) && !string.IsNullOrWhiteSpace(camera.camDevSerialNumber))
                                       ? $"{camera.camDevVendor}:{camera.camDevSerialNumber}"
                                       : (!string.IsNullOrEmpty(camera.camDevID) ? camera.camDevID : $"fallback_{index}");

            var cameraName = string.IsNullOrEmpty(camera.camDevSerialNumber)
                ? $"相机 {index + 1}"
                : $"{camera.camDevModelName} {camera.camDevSerialNumber}";

            return new CameraBasicInfo
            {
                Id = constructedCameraId,
                Name = cameraName,
                Model = camera.camDevModelName,
                SerialNumber = camera.camDevSerialNumber
            };
        }).ToList();
    }

    #endregion

    #region 私有方法

    /// <summary>
    /// 启动相机服务
    /// </summary>
    /// <param name="configurationPath">配置文件路径</param>
    /// <returns>启动结果</returns>
    private bool StartService(string? configurationPath = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(HuaRayCameraService));

        try
        {
            var configPath = configurationPath ?? GetConfigPath();

            if (string.IsNullOrEmpty(configPath))
            {
                Log.Error("无法找到LogisticsBase.cfg配置文件");
                return false;
            }

            Log.Information("正在启动华睿相机服务，配置文件路径: {ConfigPath}", configPath);

            _cts = new CancellationTokenSource();
            _eventChannel = Channel.CreateBounded<HuaRayCodeEventArgs>(new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
            _consumerTask = Task.Run(() => ConsumeChannelAsync(_cts.Token), _cts.Token);
            Log.Information("事件通道消费者已启动.");

            var result = _huaRayWrapper.Initialization(configPath);
            if (result != 0)
            {
                Log.Error("华睿相机SDK初始化失败，错误码: {ErrorCode}", result);
                _eventChannel.Writer.TryComplete();
                _cts.Cancel();
                return false;
            }

            RegisterEvents();

            result = _huaRayWrapper.Start();
            if (result != 0)
            {
                Log.Error("华睿相机SDK启动失败，错误码: {ErrorCode}", result);
                UnregisterEvents();
                _eventChannel.Writer.TryComplete();
                _cts.Cancel();
                return false;
            }

            _huaRayWrapper.AttachCameraDisconnectCb();
            IsConnected = true;
            Log.Information("华睿相机服务启动成功.");

            ServiceStarted?.Invoke();
            ConnectionChanged?.Invoke(string.Empty, true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动华睿相机服务时发生错误.");
            _eventChannel?.Writer.TryComplete(ex);
            _cts?.Cancel();
            IsConnected = false;
            return false;
        }
    }

    /// <summary>
    /// 停止相机服务
    /// </summary>
    private void StopService()
    {
        if (!IsConnected && _consumerTask == null)
        {
            return;
        }

        Log.Information("正在停止华睿相机服务...");

        try
        {
            UnregisterEvents();
            _huaRayWrapper.DetachCameraDisconnectCb();
            _huaRayWrapper.StopApp();

            _eventChannel?.Writer.TryComplete();

            if (_cts is { IsCancellationRequested: false })
            {
                _cts.Cancel();
            }

            if (_consumerTask != null)
            {
                var finished = _consumerTask.Wait(TimeSpan.FromSeconds(10));
                if (!finished)
                {
                    Log.Warning("消费者任务未在超时时间内完成.");
                }
                else
                {
                    Log.Information("消费者任务已完成.");
                }

                _consumerTask = null;
            }

            IsConnected = false;
            Log.Information("华睿相机服务已停止.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止华睿相机服务时发生错误.");
            IsConnected = false;
            ConnectionChanged?.Invoke(string.Empty, false);
        }
    }

    /// <summary>
    /// 注册事件
    /// </summary>
    private void RegisterEvents()
    {
        _huaRayWrapper.CodeHandle += OnCodeHandle;
        _huaRayWrapper.CameraDisconnectEventHandler += OnCameraDisconnect;
    }

    /// <summary>
    /// 注销事件
    /// </summary>
    private void UnregisterEvents()
    {
        _huaRayWrapper.CodeHandle -= OnCodeHandle;
        _huaRayWrapper.CameraDisconnectEventHandler -= OnCameraDisconnect;
    }

    /// <summary>
    /// 事件处理器 - 写入 Channel (Producer)
    /// </summary>
    private async void OnCodeHandle(object? sender, HuaRayCodeEventArgs args)
    {
        if (_eventChannel == null || _cts == null || _cts.IsCancellationRequested)
        {
            Log.Warning("OnCodeHandle: 通道/CTS未就绪或已请求取消。正在丢弃事件 CameraID={CameraID}.", args.CameraId);
            args.Dispose();
            return;
        }

        try
        {
            await _eventChannel.Writer.WriteAsync(args, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log.Warning("OnCodeHandle: 写入事件时操作被取消 CameraID={CameraID}. 正在丢弃.", args.CameraId);
            args.Dispose();
        }
        catch (ChannelClosedException)
        {
            Log.Warning("OnCodeHandle: 尝试写入已关闭的通道 CameraID={CameraID}. 正在丢弃.", args.CameraId);
            args.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OnCodeHandle: 写入事件到通道时出错 CameraID={CameraID}. 正在丢弃.", args.CameraId);
            args.Dispose();
        }
    }

    /// <summary>
    /// 后台任务 - 读取 Channel 并处理 (Consumer)
    /// </summary>
    private async Task ConsumeChannelAsync(CancellationToken cancellationToken)
    {
        Log.Information("事件通道消费者任务已启动.");
        try
        {
            if (_eventChannel == null)
            {
                Log.Error("ConsumeChannelAsync: 事件通道为 null.");
                return;
            }

            await foreach (var args in _eventChannel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await ProcessPackageInfoAsync(args);
                }
                catch (Exception ex)
                {
                    Log.Error(ex,
                        "ConsumeChannelAsync: 处理 ProcessPackageInfoAsync 时发生未处理异常 CameraID={CameraID}. 事件处理可能被跳过.",
                        args.CameraId);
                    Log.Warning("ConsumeChannelAsync: 在 catch 块中尝试保护性释放 args.");
                    args.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("ConsumeChannelAsync: 任务已取消. 正在退出循环.");
        }
        catch (ChannelClosedException)
        {
            Log.Information("ConsumeChannelAsync: 读取时通道已关闭. 消费者任务正在退出.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ConsumeChannelAsync: 消费者任务循环中发生未处理异常. 任务正在停止.");
        }
        finally
        {
            Log.Information("事件通道消费者任务已完成.");
        }
    }

    /// <summary>
    /// 相机断线事件处理
    /// </summary>
    private void OnCameraDisconnect(object? sender, CameraStatusArgs args)
    {
        Log.Warning("华睿相机状态变化: 相机ID={CameraID}, 状态={Status}",
            args.CameraUserId, args.IsOnline ? "在线" : "离线");

        ConnectionChanged?.Invoke(args.CameraUserId, args.IsOnline);
    }

    /// <summary>
    /// 处理包裹信息 (Called by Consumer)
    /// </summary>
    private async Task ProcessPackageInfoAsync(HuaRayCodeEventArgs args)
    {
        BitmapSource? processedBitmapSource = null;

        try
        {
            // 1. 先处理图像
            await _imageProcessingSemaphore.WaitAsync();
            try
            {
                if (args.OriginalImage.ImageData == IntPtr.Zero || args.OriginalImage.dataSize <= 0 ||
                    args.OriginalImage.width <= 0 || args.OriginalImage.height <= 0)
                {
                    Log.Warning("无效的原始图像数据: Size={DataSize}, W={Width}, H={Height}, CameraId={CameraId}",
                        args.OriginalImage.dataSize, args.OriginalImage.width, args.OriginalImage.height,
                        args.CameraId);
                }
                else
                {
                    var bitmapCreationStopwatch = Stopwatch.StartNew();
                    var imageType = (HuaRayApiStruct.EImageType)args.OriginalImage.type;

                    var currentBitmapSource =
                        imageType == HuaRayApiStruct.EImageType.EImageTypeJpeg
                            ? ProcessJpegImagePointerToBitmapSource(args.OriginalImage.ImageData,
                                args.OriginalImage.dataSize)
                            : ProcessNonJpegImagePointerToBitmapSource(args.OriginalImage.ImageData,
                                args.OriginalImage.dataSize, args.OriginalImage.width, args.OriginalImage.height,
                                imageType);
                    bitmapCreationStopwatch.Stop();
                    if (currentBitmapSource != null)
                    {
                        if (!currentBitmapSource.IsFrozen)
                        {
                            currentBitmapSource.Freeze();
                        }

                        processedBitmapSource = currentBitmapSource;
                        _imageSubject.OnNext((processedBitmapSource,
                            args.CameraId)); // Publish image stream
                    }
                    else
                    {
                        Log.Warning("图像处理未能成功创建 BitmapSource 对象, CameraId: {CameraId}, 耗时: {ElapsedMs}ms",
                            args.CameraId, bitmapCreationStopwatch.ElapsedMilliseconds);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "在信号量内处理图像数据时发生错误, CameraId: {CameraId}", args.CameraId);
            }
            finally
            {
                _imageProcessingSemaphore.Release();
            }

            // 2. 处理 OutputResult == 1 的情况
            if (args.OutputResult == 1)
            {
                // 创建 PackageInfo 对象
                var packageInfo = PackageInfo.Create();

                // 设置条码
                packageInfo.SetBarcode(args.CodeList.FirstOrDefault() ?? "noread");

                // --- 开始添加日志上下文 ---
                var packageContext = $"[包裹{packageInfo.Index}|{packageInfo.Barcode}]";
                using (LogContext.PushProperty("PackageContext", packageContext))
                {
                    Log.Debug("开始处理识别结果"); // 使用 Debug 级别标记开始

                    // 设置触发时间戳
                    try
                    {
                        if (args.TriggerTimeTicks > 0)
                        {
                            var triggerTime = DateTimeOffset.FromUnixTimeMilliseconds(args.TriggerTimeTicks).LocalDateTime;
                            packageInfo.TriggerTimestamp = triggerTime;
                            Log.Debug("设置触发时间戳: {TriggerTimestamp}", triggerTime);
                        }
                        else {
                             Log.Warning("相机事件触发时间戳 Ticks 为 0 或无效.");
                        }
                    }
                    catch (ArgumentOutOfRangeException ex)
                    {
                        Log.Error(ex, "转换 TriggerTimeTicks ({Ticks}) 失败.", args.TriggerTimeTicks);
                    }


                    // 设置重量
                    if (args.Weight > 0)
                    {
                        var weightKg = args.Weight / 1000.0;
                        packageInfo.Weight = weightKg;
                        // Log.Information("设置包裹重量: {称重模块}", args.称重模块); // 原有日志，考虑是否保留或改为 Debug
                        Log.Debug("设置包裹重量: {WeightKg} kg", weightKg); // 使用 Debug 级别记录详细信息
                    }

                    // 设置尺寸和体积
                    try
                    {
                        if (args.VolumeInfo is { length: > 0, width: > 0, height: > 0 })
                        {
                            var lengthCm = args.VolumeInfo.length / 10.0;
                            var widthCm = args.VolumeInfo.width / 10.0;
                            var heightCm = args.VolumeInfo.height / 10.0;
                            var volumeCm3 = Math.Round(args.VolumeInfo.volume / 1000.0, 2); // 假设 volume 是 mm³, 转换为 cm³

                            packageInfo.SetDimensions(lengthCm, widthCm, heightCm);
                            packageInfo.Volume = volumeCm3;
                            Log.Debug("设置包裹尺寸: L={LengthCm}cm W={WidthCm}cm H={HeightCm}cm, Vol={VolumeCm3}cm³",
                                         lengthCm, widthCm, heightCm, volumeCm3); // 使用 Debug 级别
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "设置包裹尺寸时发生错误"); // 保留 Warning 级别
                    }

                    // 设置图像
                    if (processedBitmapSource != null)
                    {
                        packageInfo.SetImage(processedBitmapSource, null); // Set the processed image
                        Log.Debug("已设置包裹图像"); // 使用 Debug 级别
                    }
                    else
                    {
                        Log.Warning("未能将图像设置到 PackageInfo，因为图像处理失败"); // 保留 Warning 级别
                    }

                    // 计算并记录处理时间
                    packageInfo.ProcessingTime = (packageInfo.CreateTime - packageInfo.TriggerTimestamp).TotalMilliseconds;
                    Log.Information("包裹处理完成, 耗时: {ProcessingTime:F0}ms", packageInfo.ProcessingTime); // 保留 Information 级别标记完成

                    // 推送包裹信息
                    _packageSubject.OnNext(packageInfo);

                }
            }
        }
        catch (Exception ex)
        {
           // 这个 catch 块在 LogContext 之外，不会有 PackageContext
           Log.Error(ex, "处理华睿相机条码事件时发生未预期的错误 (ProcessPackageInfoAsync): {Message}, CameraId: {CameraId}", ex.Message,
                args.CameraId);
        }
        finally
        {
            args.Dispose(); // 确保资源总是被释放
        }
    }

    /// <summary>
    /// 处理JPEG图像数据
    /// </summary>
    private static BitmapSource? ProcessJpegImagePointerToBitmapSource(
        IntPtr jpegDataPtr, int dataSize)
    {
        const double dpiX = 96;
        const double dpiY = 96;

        try
        {
            if (jpegDataPtr == IntPtr.Zero || dataSize <= 0) return null;

            var decompressor = DecompressorPool.Get();
            PixelFormat pixelFormat;
            int stride;
            byte[]? decompressedData;
            int width;
            int height;

            try
            {
                var retImg = decompressor.Decompress(jpegDataPtr, (ulong)dataSize, TJPixelFormats.TJPF_BGR,
                    TJFlags.FASTDCT);
                if (retImg.Data == null || retImg.Data.Length == 0)
                    throw new InvalidOperationException("JPEG解压缩数据为空");

                decompressedData = retImg.Data;
                width = retImg.Width;
                height = retImg.Height;
                pixelFormat = PixelFormats.Bgr24;
                stride = width * 3;
            }
            finally
            {
                DecompressorPool.Return(decompressor);
            }

            if (decompressedData == null || width <= 0 || height <= 0)
                return null;

            var bmpSource = BitmapSource.Create(
                width, height,
                dpiX, dpiY,
                pixelFormat,
                null,
                decompressedData,
                stride);
            return bmpSource;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理JPEG指针到BitmapSource时出错");
            return null;
        }
    }

    /// <summary>
    /// 处理非JPEG图像数据
    /// </summary>
    private static BitmapSource? ProcessNonJpegImagePointerToBitmapSource(
        IntPtr imageDataPtr, int dataSize, int width, int height,
        HuaRayApiStruct.EImageType imageType)
    {
        // 定义合理的图像尺寸上限 (例如 8192x8192)
        const int maxImageDimension = 8192;

        const double dpiX = 96;
        const double dpiY = 96;

        // 检查传入参数的有效性
        if (imageDataPtr == IntPtr.Zero || dataSize <= 0)
        {
            Log.Warning("ProcessNonJpeg: 无效的图像数据指针或大小. dataSize={DataSize}", dataSize);
            return null;
        }

        if (width <= 0 || height <= 0 || width > maxImageDimension || height > maxImageDimension)
        {
            Log.Warning("ProcessNonJpeg: 无效或过大的图像尺寸. Width={Width}, Height={Height}", width, height);
            return null;
        }

        byte[]? pixelData = null; // 声明在 try 外部，以便 finally 可以访问
        var bytesPerPixel = 1; // 在 try 外部声明并提供默认值
        try
        {
            PixelFormat pixelFormat;
            BitmapPalette? palette = null;

            switch (imageType)
            {
                case HuaRayApiStruct.EImageType.EImageTypeBgr:
                    pixelFormat = PixelFormats.Bgr24;
                    bytesPerPixel = 3;
                    break;
                case HuaRayApiStruct.EImageType.EImageTypeNormal:
                    // EImageTypeJpeg 不应在此处处理，但作为默认情况包含
                case HuaRayApiStruct.EImageType.EImageTypeJpeg:
                default:
                    pixelFormat = PixelFormats.Gray8;
                    bytesPerPixel = 1;
                    palette = BitmapPalettes.Gray256;
                    break;
            }

            // 计算所需的 stride 和 bufferSize
            // 使用 long 防止 stride * height 溢出 int
            var calculatedStride = (long)width * bytesPerPixel;
            var calculatedBufferSize = calculatedStride * height;
            
            var bufferSize = (int)calculatedBufferSize;
            var stride = (int)calculatedStride;


            // 使用 ArrayPool 租用缓冲区
            pixelData = ArrayPool<byte>.Shared.Rent(bufferSize);

            // 确定实际要拷贝的数据大小
            var copySize = Math.Min(dataSize, bufferSize);

            // 从非托管内存拷贝数据到租用的缓冲区
            Marshal.Copy(imageDataPtr, pixelData, 0, copySize);

            // 如果实际拷贝的数据小于缓冲区大小，记录警告并清空剩余部分 (防止潜在的垃圾数据)
            if (copySize < bufferSize)
            {
                Log.Warning("ProcessNonJpeg: 原始图像数据大小 ({DataSize}) 小于预期 ({BufferSize})，可能导致图像不完整。", dataSize, bufferSize);
                // 清空数组中未被覆盖的部分
                pixelData.AsSpan(copySize).Clear();
            }

            // 创建 BitmapSource 对象
            // 注意：即使使用了 ArrayPool，BitmapSource.Create 内部仍可能分配内存，如果系统极度缺乏内存，这里仍可能抛出 OutOfMemoryException
            var bmpSource = BitmapSource.Create(
                width, height,
                dpiX, dpiY,
                pixelFormat,
                palette,
                pixelData, // 直接使用租用的数组
                stride);

            // Freeze the BitmapSource to make it cross-thread accessible
            if (bmpSource.CanFreeze)
            {
                bmpSource.Freeze();
            }
            else
            {
                Log.Warning("ProcessNonJpeg: 创建的 BitmapSource 无法冻结. Width={Width}, Height={Height}", width, height);
                // 根据需要决定是否返回非冻结的 BitmapSource 或 null
            }

            return bmpSource; // 返回创建的 BitmapSource
        }
        catch (ArgumentException argEx)
        {
            // BitmapSource.Create 可能因 stride 不匹配等原因抛出 ArgumentException
            Log.Error(argEx, "ProcessNonJpeg: 创建 BitmapSource 时参数错误. Width={Width}, Height={Height}, ExpectedStride={ExpectedStride}",
                      width, height, (long)width * bytesPerPixel); // 使用已声明的 bytesPerPixel
            return null;
        }
        catch (OutOfMemoryException oomEx)
        {
            // 即使有检查和 ArrayPool，极端情况下仍可能发生 OOM
            Log.Error(oomEx, "ProcessNonJpeg: 处理图像时内存不足. Width={Width}, Height={Height}, ExpectedBufferSize={ExpectedBufferSize}",
                      width, height, (long)width * bytesPerPixel * height); // 使用已声明的 bytesPerPixel
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ProcessNonJpeg: 处理非JPEG指针到BitmapSource时发生未知错误. Width={Width}, Height={Height}", width, height);
            return null;
        }
        finally
        {
            // 确保租用的数组总是被归还给池
            if (pixelData != null)
            {
                ArrayPool<byte>.Shared.Return(pixelData);
            }
        }
    }

    /// <summary>
    /// 获取配置文件路径
    /// </summary>
    private static string? GetConfigPath()
    {
        if (string.IsNullOrEmpty(_cachedConfigPath))
        {
            _cachedConfigPath = LoadCachedConfigPath();
        }

        if (!string.IsNullOrEmpty(_cachedConfigPath) && File.Exists(_cachedConfigPath))
        {
            Log.Information("使用缓存的配置文件路径: {ConfigPath}", _cachedConfigPath);
            return _cachedConfigPath;
        }

        Log.Information("开始搜索配置文件...");
        var configPath = SearchForConfigFile();

        if (string.IsNullOrEmpty(configPath))
        {
            Log.Error("无法找到LogisticsBase.cfg配置文件");
            return null;
        }

        _cachedConfigPath = configPath;
        SaveConfigPath(configPath);
        Log.Information("找到并缓存配置文件路径: {ConfigPath}", configPath);
        return configPath;
    }

    /// <summary>
    /// 加载缓存的配置文件路径
    /// </summary>
    private static string? LoadCachedConfigPath()
    {
        try
        {
            if (File.Exists(ConfigPathStorageFile))
            {
                var path = File.ReadAllText(ConfigPathStorageFile).Trim();
                return string.IsNullOrEmpty(path) ? null : path;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取缓存的配置文件路径时发生错误");
        }

        return null;
    }

    /// <summary>
    /// 保存配置文件路径
    /// </summary>
    private static void SaveConfigPath(string configPath)
    {
        try
        {
            File.WriteAllText(ConfigPathStorageFile, configPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存配置文件路径时发生错误");
        }
    }

    /// <summary>
    /// 搜索配置文件
    /// </summary>
    private static string? SearchForConfigFile()
    {
        try
        {
            Log.Information("开始搜索LogisticsBase.cfg文件...");
            var foundFiles = new List<string>();

            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            Log.Information("检查桌面快捷方式: {Path}", desktopDir);

            var shortcutPaths = FindLpShortcuts(desktopDir);
            if (shortcutPaths.Count > 0)
            {
                Log.Information($"在桌面上找到 {shortcutPaths.Count} 个LP开头的快捷方式");
                foreach (var shortcutPath in shortcutPaths)
                {
                    var targetPath = GetShortcutTargetPath(shortcutPath);
                    if (string.IsNullOrEmpty(targetPath)) continue;
                    Log.Information("快捷方式 {ShortcutPath} 指向目标: {TargetPath}", shortcutPath, targetPath);

                    var targetDir = Path.GetDirectoryName(targetPath);
                    if (string.IsNullOrEmpty(targetDir)) continue;
                    var cfgDir = Path.Combine(targetDir, "Cfg");
                    if (!Directory.Exists(cfgDir)) continue;
                    var configPath = Path.Combine(cfgDir, "LogisticsBase.cfg");
                    if (!File.Exists(configPath)) continue;
                    Log.Information("在快捷方式目标路径下找到配置文件: {FilePath}", configPath);
                    foundFiles.Add(configPath);
                }

                if (foundFiles.Count > 0)
                {
                    var mostRecentShortcutFile = foundFiles
                        .Select(f => new FileInfo(f))
                        .Where(f => f.Exists)
                        .OrderByDescending(f => f.LastWriteTime)
                        .FirstOrDefault();

                    if (mostRecentShortcutFile != null)
                    {
                        Log.Information("通过桌面快捷方式找到配置文件: {FilePath}", mostRecentShortcutFile.FullName);
                        return mostRecentShortcutFile.FullName;
                    }
                }
            }

            var searchPaths = DriveInfo.GetDrives()
                .Where(d => d is { IsReady: true, DriveType: DriveType.Fixed })
                .Select(drive => drive.RootDirectory.FullName)
                .ToList();

            searchPaths.Add(AppDomain.CurrentDomain.BaseDirectory);
            searchPaths = searchPaths.Distinct().ToList();

            Log.Information("开始搜索LP开头文件夹中的配置文件...");

            foreach (var basePath in searchPaths)
            {
                try
                {
                    Log.Information("正在搜索目录: {Path}", basePath);
                    FindLpDirsWithConfig(basePath, foundFiles, 0, 2);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "在 {Path} 中搜索LP文件夹时发生错误", basePath);
                }
            }

            if (foundFiles.Count > 0)
            {
                if (foundFiles.Count == 1)
                {
                    Log.Information("在LP文件夹下找到唯一的配置文件: {FilePath}", foundFiles[0]);
                    return foundFiles[0];
                }

                var mostRecentLpFile = foundFiles
                    .Select(f => new FileInfo(f))
                    .Where(f => f.Exists)
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault();

                if (mostRecentLpFile != null)
                {
                    Log.Information("在LP文件夹下找到多个配置文件，选择最近修改的: {FilePath}", mostRecentLpFile.FullName);
                    return mostRecentLpFile.FullName;
                }
            }

            Log.Information("未在LP文件夹下找到配置文件，开始搜索常规位置...");
            foundFiles.Clear();

            foreach (var basePath in searchPaths)
            {
                try
                {
                    if (!Directory.Exists(basePath))
                        continue;

                    Log.Information("正在搜索目录: {Path}", basePath);

                    var configPath = Path.Combine(basePath, "LogisticsBase.cfg");
                    if (File.Exists(configPath))
                    {
                        Log.Information("在路径下找到配置文件: {FilePath}", configPath);
                        foundFiles.Add(configPath);
                    }

                    var cfgDir = Path.Combine(basePath, "Cfg");
                    if (Directory.Exists(cfgDir))
                    {
                        configPath = Path.Combine(cfgDir, "LogisticsBase.cfg");
                        if (File.Exists(configPath))
                        {
                            Log.Information("在Cfg目录下找到配置文件: {FilePath}", configPath);
                            foundFiles.Add(configPath);
                        }
                    }

                    try
                    {
                        foreach (var dir in Directory.GetDirectories(basePath))
                        {
                            try
                            {
                                configPath = Path.Combine(dir, "LogisticsBase.cfg");
                                if (File.Exists(configPath))
                                {
                                    Log.Information("在子目录下找到配置文件: {FilePath}", configPath);
                                    foundFiles.Add(configPath);
                                }

                                cfgDir = Path.Combine(dir, "Cfg");
                                if (!Directory.Exists(cfgDir)) continue;
                                configPath = Path.Combine(cfgDir, "LogisticsBase.cfg");
                                if (!File.Exists(configPath)) continue;
                                Log.Information("在子目录的Cfg目录下找到配置文件: {FilePath}", configPath);
                                foundFiles.Add(configPath);
                            }
                            catch (UnauthorizedAccessException)
                            {
                                // 忽略权限问题
                            }
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // 忽略权限问题
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "搜索路径 {Path} 时发生错误", basePath);
                }
            }

            switch (foundFiles.Count)
            {
                case 0:
                    Log.Warning("未找到LogisticsBase.cfg文件");
                    return null;
                case 1:
                    return foundFiles[0];
            }

            var mostRecentFile = foundFiles
                .Select(f => new FileInfo(f))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();

            if (mostRecentFile == null)
            {
                Log.Warning("找到的配置文件均无效");
                return null;
            }

            Log.Information("找到多个配置文件，选择最近修改的文件: {FilePath}", mostRecentFile.FullName);
            return mostRecentFile.FullName;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "搜索配置文件时发生错误");
            return null;
        }
    }

    /// <summary>
    /// 查找LP目录下的配置文件
    /// </summary>
    private static void FindLpDirsWithConfig(string directory, List<string> foundFiles, int currentDepth, int maxDepth)
    {
        if (currentDepth > maxDepth)
            return;

        if (!Directory.Exists(directory))
            return;

        try
        {
            var dirInfo = new DirectoryInfo(directory);
            if (dirInfo.Name.StartsWith("LP", StringComparison.OrdinalIgnoreCase))
            {
                var cfgDir = Path.Combine(directory, "Cfg");
                if (Directory.Exists(cfgDir))
                {
                    var configPath = Path.Combine(cfgDir, "LogisticsBase.cfg");
                    if (File.Exists(configPath))
                    {
                        Log.Information("在LP文件夹下的Cfg目录中找到配置文件: {FilePath}", configPath);
                        foundFiles.Add(configPath);
                    }
                }
            }

            if (currentDepth == maxDepth)
                return;

            foreach (var subDir in Directory.GetDirectories(directory))
            {
                try
                {
                    FindLpDirsWithConfig(subDir, foundFiles, currentDepth + 1, maxDepth);
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "搜索子目录 {Path} 时发生错误", subDir);
                }

                if (foundFiles.Count > 0 && currentDepth > 0)
                    return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 忽略权限问题
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "访问目录 {Path} 时发生错误", directory);
        }
    }

    /// <summary>
    /// 查找LP相关快捷方式
    /// </summary>
    private static List<string> FindLpShortcuts(string directory)
    {
        var result = new List<string>();

        try
        {
            if (!Directory.Exists(directory))
                return result;

            foreach (var file in Directory.GetFiles(directory, "*.lnk"))
            {
                try
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (!fileName.Contains("LogisticsPlatformApp", StringComparison.OrdinalIgnoreCase) &&
                        !fileName.Contains("华睿", StringComparison.OrdinalIgnoreCase) &&
                        !fileName.Contains("HuaRay", StringComparison.OrdinalIgnoreCase) &&
                        !fileName.StartsWith("LP", StringComparison.OrdinalIgnoreCase)) continue;
                    Log.Information("找到可能的相机程序快捷方式: {ShortcutName}", fileName);
                    result.Add(file);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "处理快捷方式 {Path} 时发生错误", file);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "在目录 {Directory} 中搜索快捷方式时发生错误", directory);
        }

        return result;
    }

    /// <summary>
    /// 获取快捷方式目标路径
    /// </summary>
    private static string? GetShortcutTargetPath(string shortcutPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments =
                    $"-Command \"(New-Object -ComObject WScript.Shell).CreateShortcut('{shortcutPath.Replace("'", "''")}').TargetPath\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                Log.Warning("无法启动PowerShell进程解析快捷方式");
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            if (!string.IsNullOrEmpty(output)) return output;
            Log.Warning("无法获取快捷方式 {Path} 的目标路径", shortcutPath);
            return null;

        }
        catch (Exception ex)
        {
            Log.Warning(ex, "解析快捷方式 {Path} 时发生错误", shortcutPath);
            return null;
        }
    }

    #endregion
}
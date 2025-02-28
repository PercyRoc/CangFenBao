using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using CommonLibrary.Models;
using CommonLibrary.Models.Settings.Camera;
using DeviceService.Camera.Models;
using LogisticsBaseCSharp;
using Microsoft.Extensions.ObjectPool;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TurboJpegWrapper;

namespace DeviceService.Camera.DaHua;

/// <summary>
///     大华相机服务
/// </summary>
public class DahuaCameraService : ICameraService
{
    private static readonly DefaultObjectPool<TJDecompressor> DecompressorPool =
        new(new DefaultPooledObjectPolicy<TJDecompressor>(), 2);

    private readonly object _cameraIdLock = new();
    private readonly SemaphoreSlim _imageProcessingSemaphore = new(1, 1);
    private readonly Subject<(Image<Rgba32> image, IReadOnlyList<BarcodeLocation> barcodes)> _imageSubject = new();
    private readonly LogisticsWrapper _logisticsWrapper;
    private readonly Subject<PackageInfo> _packageSubject = new();
    private bool _disposed;
    private string? _firstCameraId;

    /// <summary>
    ///     构造函数
    /// </summary>
    public DahuaCameraService()
    {
        _logisticsWrapper = LogisticsWrapper.Instance;
    }

    /// <summary>
    ///     相机连接状态改变事件
    /// </summary>
    public event Action<string, bool>? ConnectionChanged;

    /// <summary>
    ///     相机是否已连接
    /// </summary>
    public bool IsConnected { get; private set; }

    /// <summary>
    ///     包裹信息流
    /// </summary>
    public IObservable<PackageInfo> PackageStream => _packageSubject.AsObservable();

    /// <summary>
    ///     图像信息流
    /// </summary>
    public IObservable<(Image<Rgba32> image, IReadOnlyList<BarcodeLocation> barcodes)> ImageStream =>
        _imageSubject.AsObservable();

    /// <summary>
    ///     启动相机服务
    /// </summary>
    public bool Start()
    {
        if (IsConnected)
        {
            Log.Warning("相机服务已经启动");
            return true;
        }

        try
        {
            Log.Information("正在启动相机服务...");

            // 初始化SDK
            var initResult = Initialize();
            if (initResult != 0)
            {
                Log.Error("初始化SDK失败：{Result}", initResult);
                return false;
            }

            // 注册读码信息回调
            try
            {
                _logisticsWrapper.AllCameraCodeInfoEventHandler += (_, _) => { };
                _logisticsWrapper.AttachAllCameraCodeinfoCB();
                Log.Information("已注册读码信息回调");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "注册读码回调时发生错误");
            }

            // 注册包裹信息回调
            _logisticsWrapper.CodeHandle += (_, args) =>
            {
                try
                {
                    // 检查原始图像数据是否有效
                    if (args.OriginalImage.ImageData == IntPtr.Zero || args.OriginalImage.dataSize <= 0)
                    {
                        Log.Warning("相机 {IP} 的原始图像数据无效", args.CameraID);
                        return;
                    }

                    // 记录第一次收到的相机ID，用于实时图像处理
                    if (_firstCameraId == null)
                        lock (_cameraIdLock)
                        {
                            if (_firstCameraId == null)
                            {
                                _firstCameraId = args.CameraID;
                                Log.Information("设置首次处理图像的相机ID: {CameraId}", _firstCameraId);
                            }
                        }

                    if (args.OutputResult == 1)
                    {
                        // 处理包裹数据（处理所有相机）
                        var startTime = DateTime.Now;
                        var barcode = args.CodeList?.FirstOrDefault() ?? string.Empty;
                        Log.Information("开始处理包裹项 - 条码: {Barcode} 时间: {Time} 相机: {CameraId}", barcode, startTime,
                            args.CameraID);

                        var packageInfo = new PackageInfo
                        {
                            Barcode = barcode,
                            CreateTime = startTime
                        };

                        // 设置重量和尺寸数据
                        if (args.Weight > 0) packageInfo.Weight = (float)(args.Weight / 1000.0);

                        if (args.VolumeInfo is { length: > 0, width: > 0, height: > 0 })
                        {
                            // 将毫米转换为厘米存储和显示
                            packageInfo.Length = Math.Round(args.VolumeInfo.length / 10, 2);
                            packageInfo.Width = Math.Round(args.VolumeInfo.width / 10, 2);
                            packageInfo.Height = Math.Round(args.VolumeInfo.height / 10, 2);
                            packageInfo.VolumeDisplay =
                                $"{packageInfo.Length:F1} × {packageInfo.Width:F1} × {packageInfo.Height:F1}";
                        }

                        // 处理图像数据
                        try
                        {
                            var imageStartTime = DateTime.Now;

                            // 复制图像数据到托管内存
                            var imageData = new byte[args.OriginalImage.dataSize];
                            Marshal.Copy(args.OriginalImage.ImageData, imageData, 0, args.OriginalImage.dataSize);

                            var (image, _) = ProcessImageData(
                                imageData,
                                args.OriginalImage.width,
                                args.OriginalImage.height,
                                args.AreaList?.Select(points => points.Select(p => new Point(p.X, p.Y)).ToList())
                                    .ToList(),
                                (LogisticsAPIStruct.EImageType)args.OriginalImage.type
                            );

                            // 设置包裹图像
                            packageInfo.Image = image.Clone();

                            // 如果是第一个相机，则发布图像事件
                            if (args.CameraID == _firstCameraId)
                            {
                                var locations = args.AreaList?
                                    .Select(points =>
                                        new BarcodeLocation(points.Select(p => new Point(p.X, p.Y)).ToList()))
                                    .ToList() ?? [];
                                _imageSubject.OnNext((image, locations));
                            }

                            var imageProcessTime = DateTime.Now - imageStartTime;
                            Log.Debug("图像处理耗时: {Time}ms", imageProcessTime.TotalMilliseconds);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "处理图像数据失败");
                        }

                        _packageSubject.OnNext(packageInfo);
                    }
                    else
                    {
                        // 实时图像只处理第一个相机的数据
                        if (args.CameraID == _firstCameraId) ProcessRealTimeImage(args);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理包裹信息回调时发生错误");
                }
            };

            // 启动SDK
            var startResult = _logisticsWrapper.Start();
            if (startResult != 0)
            {
                Log.Error("启动SDK失败：{Result}", startResult);
                return false;
            }

            IsConnected = true;
            ConnectionChanged?.Invoke(_firstCameraId ?? string.Empty, true);
            Log.Information("相机服务启动成功");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动相机服务失败");
            return false;
        }
    }

    /// <summary>
    ///     异步停止相机服务，带超时控制
    /// </summary>
    /// <param name="timeoutMs">超时时间（毫秒）</param>
    /// <returns>操作是否成功</returns>
    public Task<bool> StopAsync(int timeoutMs = 3000)
    {
        if (!IsConnected)
        {
            Log.Debug("相机服务尚未启动，无需停止");
            return Task.FromResult(true);
        }

        try
        {
            Log.Information("正在停止大华相机服务...");

            // 同步执行停止流程
            try
            {
                // 清理回调
                DetachAllCallbacks();

                // 重置状态
                IsConnected = false;
                ConnectionChanged?.Invoke(_firstCameraId ?? string.Empty, false);

                Log.Information("大华相机服务已停止");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "停止大华相机时发生异常");
                IsConnected = false; // 强制重置状态
                return Task.FromResult(false);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止大华相机服务时发生错误");
            IsConnected = false; // 强制重置状态
            return Task.FromResult(false);
        }
    }

    /// <summary>
    ///     获取相机信息列表
    /// </summary>
    public IEnumerable<DeviceCameraInfo>? GetCameraInfos()
    {
        try
        {
            var cameraInfos = _logisticsWrapper.GetWorkCameraInfo();

            return cameraInfos?.Select(info => new DeviceCameraInfo
            {
                SerialNumber = info.camDevSerialNumber,
                Model = info.camDevModelName,
                IpAddress = info.camDevID,
                MacAddress = info.camDevExtraInfo
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取相机信息列表失败");
            return null;
        }
    }

    /// <summary>
    ///     更新相机配置
    /// </summary>
    public void UpdateConfiguration(CameraSettings config)
    {
        try
        {
            // TODO: 实现配置更新逻辑
            Log.Information("相机配置已更新");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新相机配置失败");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync();
        _imageProcessingSemaphore.Dispose();
        _packageSubject.Dispose();
        _imageSubject.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }


    /// <summary>
    ///     初始化SDK
    /// </summary>
    /// <param name="cfgPath">配置文件路径</param>
    /// <returns>初始化结果</returns>
    private int Initialize(string cfgPath = @"Cfg\LogisticsBase.cfg")
    {
        var fullPath = Path.Combine(AppContext.BaseDirectory, cfgPath);
        try
        {
            if (!File.Exists(fullPath)) throw new FileNotFoundException($"大华相机配置文件不存在：{fullPath}");

            // 清理现有的事件处理程序
            DetachAllCallbacks();

            // 设置初始化超时
            var initTask = Task.Run(() => _logisticsWrapper.Initialization(fullPath));
            if (!initTask.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("SDK初始化超时");

            return initTask.Result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化SDK失败");
            throw;
        }
    }

    /// <summary>
    ///     清理所有回调
    /// </summary>
    private void DetachAllCallbacks()
    {
        try
        {
            Log.Information("正在清理所有回调...");

            // 停止处理线程
            IsConnected = false;
            // 取消读码信息回调
            _logisticsWrapper.DetachAllCameraCodeinfoCB();
            Log.Debug("已取消读码信息回调");
            Log.Information("所有回调已清理");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "清理回调时发生错误");
        }
    }

    /// <summary>
    ///     处理图像数据
    /// </summary>
    private static (Image<Rgba32> image, IReadOnlyList<BarcodeLocation> barcodes) ProcessImageData(
        byte[] imageData,
        int width,
        int height,
        List<List<Point>>? barcodeLocations,
        LogisticsAPIStruct.EImageType imageType)
    {
        try
        {
            // 验证输入参数
            if (imageData == null || imageData.Length == 0)
                throw new ArgumentException("图像数据为空");

            if (width <= 0 || height <= 0)
                throw new ArgumentException($"无效的图像尺寸: {width}x{height}");

            // 记录图像信息
            Log.Debug("处理图像数据: 类型={ImageType}, 宽度={Width}, 高度={Height}, 数据大小={DataSize}",
                imageType, width, height, imageData.Length);

            Image<Rgba32> image;
            switch (imageType)
            {
                case LogisticsAPIStruct.EImageType.eImageTypeJpeg:
                {
                    // 获取解压缩器
                    var decompressor = DecompressorPool.Get();
                    try
                    {
                        // 解压缩JPEG数据
                        var retImg = decompressor.Decompress(imageData, TJPixelFormats.TJPF_GRAY, TJFlags.NONE);
                        if (retImg.Data == null || retImg.Data.Length == 0)
                            throw new InvalidOperationException("JPEG解压缩后的数据为空");

                        Log.Debug("JPEG解压完成: 宽度={Width}, 高度={Height}, 数据大小={DataSize}",
                            retImg.Width, retImg.Height, retImg.Data.Length);

                        // 创建新的图像数据，考虑4字节对齐
                        var stride = (retImg.Width + 3) & ~3;
                        var alignedData = new byte[stride * retImg.Height];

                        // 逐行复制数据，处理非4字节对齐的情况
                        if (retImg.Width % 4 != 0)
                            for (var i = 0; i < retImg.Height; i++)
                                Array.Copy(retImg.Data, i * retImg.Width, alignedData, i * stride, retImg.Width);
                        else
                            Array.Copy(retImg.Data, alignedData, retImg.Data.Length);

                        // 从处理后的数据创建图像
                        using var grayImage = Image.LoadPixelData<L8>(alignedData, retImg.Width, retImg.Height);
                        image = grayImage.CloneAs<Rgba32>();

#if DEBUG
                        image.SaveAsPng($"debug_image_{DateTime.Now:yyyyMMdd_HHmmss}.png");
#endif
                    }
                    finally
                    {
                        DecompressorPool.Return(decompressor);
                    }

                    break;
                }
                case LogisticsAPIStruct.EImageType.eImageTypeBGR:
                case LogisticsAPIStruct.EImageType.eImageTypeNormal:
                {
                    // 检查数据大小是否匹配mono8格式
                    var channels = imageType == LogisticsAPIStruct.EImageType.eImageTypeBGR ? 3 : 1;
                    var stride = (width * channels + 3) & ~3;
                    var expectedSize = stride * height;

                    if (imageData.Length < expectedSize)
                    {
                        Log.Warning("图像数据大小不匹配: 期望={Expected}, 实际={Actual}, 步幅={Stride}",
                            expectedSize, imageData.Length, stride);
                        throw new ArgumentException($"图像数据大小不匹配: 期望{expectedSize}字节，实际{imageData.Length}字节");
                    }

                    // 创建新的图像数据，考虑4字节对齐
                    var alignedData = new byte[stride * height];

                    // 逐行复制数据，处理非4字节对齐的情况
                    if (width * channels % 4 != 0)
                        for (var i = 0; i < height; i++)
                            Array.Copy(imageData, i * width * channels, alignedData, i * stride, width * channels);
                    else
                        Array.Copy(imageData, alignedData, imageData.Length);

                    // 从处理后的数据创建图像
                    if (channels == 1)
                    {
                        using var grayImage = Image.LoadPixelData<L8>(alignedData, width, height);
                        image = grayImage.CloneAs<Rgba32>();
                    }
                    else
                    {
                        using var bgrImage = Image.LoadPixelData<Bgr24>(alignedData, width, height);
                        image = bgrImage.CloneAs<Rgba32>();
                    }

                    break;
                }
                default:
                    throw new ArgumentException($"不支持的图像类型：{imageType}");
            }

            var locations = barcodeLocations?
                .Select(points => new BarcodeLocation(points))
                .ToList() ?? [];

            return (image, locations);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理图像数据时发生错误: 宽度={Width}, 高度={Height}, 数据大小={DataSize}, 图像类型={ImageType}",
                width, height, imageData?.Length ?? 0, imageType);
            throw;
        }
    }

    /// <summary>
    ///     处理实时图像
    /// </summary>
    private void ProcessRealTimeImage(LogisticsCodeEventArgs args)
    {
        try
        {
            // 复制图像数据到托管内存
            byte[]? imageData;
            try
            {
                imageData = new byte[args.OriginalImage.dataSize];
                Marshal.Copy(args.OriginalImage.ImageData, imageData, 0, args.OriginalImage.dataSize);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "复制图像数据时发生错误");
                return;
            }

            // 启动新线程处理图像流
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // 使用信号量控制图像处理的并发
                    _imageProcessingSemaphore.Wait();
                    try
                    {
                        var (image, barcodeLocations) = ProcessImageData(
                            imageData,
                            args.OriginalImage.width,
                            args.OriginalImage.height,
                            args.AreaList?.Select(points => points.Select(p => new Point(p.X, p.Y)).ToList()).ToList(),
                            (LogisticsAPIStruct.EImageType)args.OriginalImage.type
                        );
                        _imageSubject.OnNext((image, barcodeLocations));
                    }
                    finally
                    {
                        _imageProcessingSemaphore.Release();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "图像流处理线程发生错误");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理实时图像时发生错误");
        }
    }
}
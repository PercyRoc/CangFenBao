using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;
using Common.Models.Package;
using DeviceService.DataSourceDevices.Camera.Models;
using Serilog;

namespace DeviceService.DataSourceDevices.Camera.RenJia;

/// <summary>
///     测量结果
/// </summary>
public record MeasureResult(
    bool IsSuccess,
    string? ErrorMessage = null,
    float Length = 0,
    float Width = 0,
    float Height = 0);

/// <summary>
///     尺寸刻度图结果
/// </summary>
public record DimensionImagesResult(
    bool IsSuccess,
    string? ErrorMessage = null,
    BitmapSource? VerticalViewImage = null, // 俯视图
    BitmapSource? SideViewImage = null // 侧视图
);

/// <summary>
///     人加体积相机服务
/// </summary>
public class RenJiaCameraService : ICameraService
{
    private readonly Subject<(BitmapSource Image, string CameraId)> _imageSubject = new();
    private readonly Subject<PackageInfo> _packageSubject = new();
    private readonly CancellationTokenSource _processingCancellation = new();
    private bool _disposed;
    private bool _isConnected;

    public event Action<string, bool>? ConnectionChanged;

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (_isConnected == value) return;

            _isConnected = value;
            ConnectionChanged?.Invoke("RenJia", value);
        }
    }

    public IObservable<PackageInfo> PackageStream
    {
        get => _packageSubject.AsObservable();
    }

    public IObservable<BitmapSource> ImageStream
    {
        get => _imageSubject.Select(tuple => tuple.Image);
    }

    public IObservable<(BitmapSource Image, string CameraId)> ImageStreamWithId
    {
        get => _imageSubject.AsObservable();
    }

    public bool Start()
    {
        try
        {
            Log.Information("开始启动人加体积相机服务...");

            // 1. 关闭可能存在的后台程序
            if (!NativeMethods.KillProcess()) Log.Warning("关闭已存在的后台程序失败");
            Thread.Sleep(100);

            // 2. 启动后台程序
            NativeMethods.StartProcess();
            Thread.Sleep(200);

            // 3. 扫描设备
            var deviceNum = NativeMethods.ScanDevice();
            if (deviceNum <= 0)
            {
                Log.Error("未发现人加体积相机设备");
                return false;
            }

            // 4. 打开设备
            var result = NativeMethods.OpenDevice();
            if (result != 0)
            {
                Log.Error("打开人加体积相机失败：{Result}", result);
                return false;
            }

            // 5. 等待系统状态就绪
            const int maxRetries = 10; // 最多尝试10次
            const int retryInterval = 500; // 每次间隔500ms
            var state = new int[1];

            for (var i = 0; i < maxRetries; i++)
            {
                var stateResult = NativeMethods.GetSystemState(state);
                if (stateResult != 0)
                {
                    Log.Warning("获取系统状态失败：{Result}", stateResult);
                    Thread.Sleep(retryInterval);
                    continue;
                }

                if (state[0] == 1)
                {
                    Log.Information("人加体积相机系统状态就绪");
                    IsConnected = true;
                    return true;
                }

                Log.Debug("等待系统状态就绪，当前状态：{State}，重试次数：{Count}", state[0], i + 1);
                Thread.Sleep(retryInterval);
            }

            Log.Error("等待系统状态就绪超时");
            Stop();
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动人加体积相机服务失败");
            return false;
        }
    }

    /// <summary>
    ///     异步停止相机服务，带超时控制
    /// </summary>
    /// <returns>操作是否成功</returns>
    public bool Stop()
    {
        if (!IsConnected) return true;

        try
        {
            Log.Information("正在停止体积相机服务...");

            // 1. 关闭设备
            var closeResult = NativeMethods.CloseDevice();
            if (closeResult != 0) Log.Warning("关闭设备失败：{Result}", closeResult);

            // 2. 关闭后台程序
            var success = NativeMethods.KillProcess();
            if (!success) Log.Warning("关闭后台程序失败");

            // 重置状态
            IsConnected = false;

            Log.Information("体积相机服务已停止");
            return success;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止体积相机服务时发生错误");
            IsConnected = false; // 强制重置状态
            return false;
        }
    }

    /// <summary>
    ///     异步释放资源
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            Log.Information("正在异步释放人加体积相机资源...");
            // 释放其他资源
            try
            {
                _packageSubject.Dispose();
                _imageSubject.Dispose();
                _processingCancellation.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放资源时发生错误");
            }

            Log.Information("人加体积相机资源已异步释放完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "异步释放人加体积相机资源时发生错误");
        }
        finally
        {
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    // Implement the GetAvailableCameras method
    public IEnumerable<CameraBasicInfo> GetAvailableCameras()
    {
        // RenJia typically represents a single volume measurement device.
        // Return a list containing one entry if connected.
        if (IsConnected)
        {
            return new List<CameraBasicInfo>
            {
                new()
                {
                    Id = "RenJia_0",
                    Name = "人加体积相机"
                } // Provide default info
            };
        }
        return []; // Return empty list if not connected
    }

    /// <summary>
    ///     触发一次测量
    /// </summary>
    /// <returns>测量结果</returns>
    public MeasureResult TriggerMeasure()
    {
        try
        {
            var startTime = DateTime.Now;

            // 创建测量任务
            var measureTask = Task.Run(() =>
            {
                try
                {
                    // 0. 检查设备状态
                    var state = new int[1];
                    var stateResult = NativeMethods.GetSystemState(state);
                    if (stateResult != 0)
                    {
                        Log.Warning("触发测量前获取设备状态失败：{Result}", stateResult);
                        return new MeasureResult(false, "获取设备状态失败");
                    }
                    if (state[0] != 1) // 1 表示就绪
                    {
                        Log.Warning("设备状态异常，无法触发测量。当前状态：{State}", state[0]);
                        return new MeasureResult(false, $"设备状态异常 ({state[0]})");
                    }

                    // 1. 执行测量（非阻塞）
                    var computeResult = NativeMethods.ComputeOnce();
                    if (computeResult != 0)
                    {
                        Log.Warning("触发测量失败：{Result}", computeResult);
                        return new MeasureResult(false, "触发测量失败");
                    }

                    // 2. 获取测量结果 (不再需要在此处检查状态)
                    var dimensionData = new float[3];
                    var imageDataBuffer = new byte[10 * 1024 * 1024];

                    var len = NativeMethods.GetDmsResult(dimensionData, imageDataBuffer);
                    if (len <= 0)
                    {
                        var error = GetErrorMessage();
                        Log.Warning("获取测量结果失败：{Error}", error);
                        return new MeasureResult(false, error);
                    }

                    // 3. 验证测量结果
                    if (dimensionData.Any(static d => float.IsNaN(d) || float.IsInfinity(d) || d is <= 0 or > 2000))
                    {
                        var error = $"测量结果无效：L={dimensionData[0]}, W={dimensionData[1]}, H={dimensionData[2]}";
                        Log.Warning(error);
                        // Even if dimensions are invalid, the image might be useful
                        // Try processing the image anyway before returning failure.
                        // return new MeasureResult(false, error); // Moved image processing up
                    }

                    // 4. 处理图像数据 (JPEG from GetDmsResult)
                    try
                    {
                        // Create stream directly from the buffer segment returned by GetDmsResult
                        using var memoryStream = new MemoryStream(imageDataBuffer, 0, len);
                        var bitmapImage = new BitmapImage();
                        bitmapImage.BeginInit();
                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad; // Read immediately
                        bitmapImage.StreamSource = memoryStream;
                        bitmapImage.EndInit();
                        bitmapImage.Freeze(); // Ensure can be used across threads

                        _imageSubject.OnNext((bitmapImage, "RenJia_0")); // Assuming "RenJia_0" is the ID for the main camera
                        Log.Debug("成功处理来自 GetDmsResult 的图像数据 (JPEG, 大小: {Size} bytes)", len);
                    }
                    catch (NotSupportedException nex)
                    {
                        Log.Warning(nex, "转换来自 GetDmsResult 的图像数据失败，确认数据是否为有效的 JPEG 格式 (大小: {Size})", len);
                        // Log prefix if helpful
                        var prefix = string.Empty;
                        const int prefixLength = 32;
                        try { prefix = Convert.ToBase64String(imageDataBuffer, 0, Math.Min(len, prefixLength)); }
                        catch
                        {
                            // ignored
                        }

                        Log.Warning("Data Prefix (Base64): {Prefix}", prefix);
                        // Image processing failed, but dimensions might still be valid
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "处理来自 GetDmsResult 的图像数据时发生错误 (大小: {Size})", len);
                        // Image processing failed, but dimensions might still be valid
                    }

                    // Check dimensions validity again *after* trying image processing
                    if (dimensionData.Any(static d => float.IsNaN(d) || float.IsInfinity(d) || d is <= 0 or > 2000))
                    {
                        var error = $"测量结果无效：L={dimensionData[0]}, W={dimensionData[1]}, H={dimensionData[2]}";
                        return new MeasureResult(false, error);
                    }

                    var duration = (DateTime.Now - startTime).TotalMilliseconds;
                    Log.Information("测量成功：{Length}x{Width}x{Height}mm，耗时：{Duration:F2}ms",
                        dimensionData[0], dimensionData[1], dimensionData[2], duration);

                    return new MeasureResult(
                        true,
                        Length: dimensionData[0],
                        Width: dimensionData[1],
                        Height: dimensionData[2]
                    );
                }
                catch (AccessViolationException ex)
                {
                    Log.Fatal(ex, "测量过程中发生内存访问冲突");
                    return new MeasureResult(false, "硬件通信异常");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "测量过程中发生异常");
                    return new MeasureResult(false, ex.Message);
                }
            });

            // 无限期等待测量任务完成
            return measureTask.Result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行测量时发生异常");
            return new MeasureResult(false, ex.Message);
        }
    }

    private static string GetErrorMessage()
    {
        try
        {
            var errorMessage = new byte[256];
            var messageLength = NativeMethods.GetErrorMes(errorMessage);
            return messageLength <= 0 ? "未知错误" : Encoding.UTF8.GetString(errorMessage, 0, messageLength).TrimEnd('\0');
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取错误信息时发生错误");
            return "获取错误信息失败";
        }
    }

    /// <summary>
    ///     设置托盘高度
    /// </summary>
    /// <param name="palletHeightMm">托盘高度（毫米）</param>
    /// <returns>0表示成功，其他值表示失败</returns>
    public int SetPalletHeight(int palletHeightMm)
    {
        try
        {
            if (!IsConnected)
            {
                Log.Warning("相机未连接，无法设置托盘高度");
                return -1;
            }

            var result = NativeMethods.SetPalletHeight(palletHeightMm);
            if (result != 0)
            {
                Log.Warning("设置托盘高度失败：{Result}", result);
            }
            else
            {
                Log.Information("成功设置托盘高度：{Height}mm", palletHeightMm);
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设置托盘高度时发生错误");
            return -1;
        }
    }

    /// <summary>
    ///     异步获取尺寸刻度图 (俯视图和侧视图)
    /// </summary>
    /// <returns>包含图像的 DimensionImagesResult 对象</returns>
    public async Task<DimensionImagesResult> GetDimensionImagesAsync()
    {
        if (!IsConnected)
        {
            Log.Warning("相机未连接，无法获取尺寸刻度图");
            return new DimensionImagesResult(false, "相机未连接");
        }

        try
        {
            return await Task.Run(() =>
            {
                const int bufferSize = 10 * 1024 * 1024; // 假设与 GetDmsResult 缓冲区大小相同
                var verticalViewBuffer = new byte[bufferSize];
                var sideViewBuffer = new byte[bufferSize];

                var result = NativeMethods.GetDimensionImage(verticalViewBuffer, sideViewBuffer);

                if (result != 0)
                {
                    var error = GetErrorMessage();
                    Log.Warning("获取尺寸刻度图失败：{Result} - {Error}", result, error);
                    return new DimensionImagesResult(false, $"获取图像失败: {error}");
                }

                // 假设成功时，DLL将实际图像大小的数据填充到缓冲区开头 (4 bytes size + data)
                BitmapSource? verticalImage = null;
                BitmapSource? sideImage = null;
                string? conversionError = null;

                try
                {
                    // 尝试转换俯视图
                    verticalImage = ConvertBytesToBitmapSource(verticalViewBuffer, "俯视图");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理或转换俯视图图像失败");
                    conversionError = "转换俯视图失败";
                }

                try
                {
                    // 尝试转换侧视图
                    sideImage = ConvertBytesToBitmapSource(sideViewBuffer, "侧视图");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理或转换侧视图图像失败");
                    if (conversionError != null) conversionError += "; ";
                    conversionError += "转换侧视图失败";
                }

                if (verticalImage != null || sideImage != null)
                {
                    Log.Information("成功获取尺寸刻度图 (俯视图: {VStatus}, 侧视图: {SStatus})",
                        verticalImage != null ? "成功" : "失败",
                        sideImage != null ? "成功" : "失败");
                    return new DimensionImagesResult(true, conversionError, verticalImage, sideImage);
                }
                return new DimensionImagesResult(false, conversionError ?? "无法转换任何图像");

            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取尺寸刻度图时发生异常");
            return new DimensionImagesResult(false, $"异常: {ex.Message}");
        }
    }

    private static BitmapSource? ConvertBytesToBitmapSource(byte[] buffer, string imageNameForLogging)
    {
        if (buffer.Length < 4) // Need at least 4 bytes for the size
        {
            Log.Warning("缓冲区太小，无法读取 {ImageName} 图像大小 (Length: {Length})", imageNameForLogging, buffer.Length);
            return null;
        }

        // Read the size from the first 4 bytes (little-endian)
        var imageSize = BitConverter.ToInt32(buffer, 0);

        const int headerSize = 4;
        var remainingBufferSize = buffer.Length - headerSize;

        if (imageSize <= 0 || imageSize > remainingBufferSize)
        {
            // Plausible check for PNG signature anyway if size looks wrong
            const int pngSignatureOffset = headerSize; // Expected offset of PNG signature
            var signatureBytes = new byte[]
            {
                0x89, 0x50, 0x4E, 0x47
            }; // PNG signature
            if (buffer.Length >= pngSignatureOffset + signatureBytes.Length &&
                buffer.Skip(pngSignatureOffset).Take(signatureBytes.Length).SequenceEqual(signatureBytes))
            {
                Log.Warning("读取的 {ImageName} 图像大小无效 ({Size}), 但在偏移量 {Offset} 处找到PNG签名。尝试直接从签名开始解码整个剩余缓冲区。",
                    imageNameForLogging, imageSize, pngSignatureOffset);
                // Fallback: Try decoding from the PNG signature onwards, using the rest of the buffer. Might still fail.
                imageSize = remainingBufferSize; // Use the maximum possible size as a guess
                // If this still fails, the data is likely corrupt or the size interpretation is wrong.
            }
            else
            {
                Log.Warning("读取的 {ImageName} 图像大小无效或超出缓冲区范围 (Size: {Size}, Buffer Remaining: {Remaining}), 且未找到PNG签名。无法解码。",
                    imageNameForLogging, imageSize, remainingBufferSize);
                return null;
            }

        }

        try
        {
            // Create a stream over the actual image data segment
            using var stream = new MemoryStream(buffer, headerSize, imageSize);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze(); // Important for cross-thread access
            Log.Debug("成功解码 {ImageName} 图像 (大小: {Size} bytes)", imageNameForLogging, imageSize);
            return bitmap;
        }
        catch (NotSupportedException nex) // Often indicates invalid image format within the segment
        {
            // Log details for debugging
            var prefix = string.Empty;
            const int prefixLength = 32; // Log first 32 bytes of the *segment*
            if (imageSize > 0)
            {
                try
                {
                    prefix = Convert.ToBase64String(buffer, headerSize, Math.Min(imageSize, prefixLength));
                }
                catch
                { /* Ignore potential errors during prefix generation */
                }
            }
            Log.Warning(nex, "转换 {ImageName} 图像数据失败 (在偏移量 {Offset}, 尝试长度 {Length})，可能格式无效或数据损坏。Segment Prefix (Base64): {Prefix}",
                imageNameForLogging, headerSize, imageSize, prefix);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "将 {ImageName} 字节数组转换为 BitmapSource 时发生错误 (Size: {Size})", imageNameForLogging, imageSize);
            return null;
        }
    }
}

/// <summary>
///     人加体积相机原生方法
/// </summary>
internal static class NativeMethods
{
    // 关闭后台应用程序
    [DllImport("VolumeMeasurementDll.dll", EntryPoint = "KillProcess", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool KillProcess();

    // 开启后台应用程序
    [DllImport("VolumeMeasurementDll.dll", EntryPoint = "StartProcess", CallingConvention = CallingConvention.Cdecl)]
    public static extern void StartProcess();

    // 扫描设备，返回在线设备数量
    [DllImport("VolumeMeasurementDll.dll", EntryPoint = "ScanDevice", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ScanDevice();

    // 开启设备
    [DllImport("VolumeMeasurementDll.dll", EntryPoint = "OpenDevice", CallingConvention = CallingConvention.Cdecl)]
    public static extern int OpenDevice();

    // 关闭设备
    [DllImport("VolumeMeasurementDll.dll", EntryPoint = "CloseDevice", CallingConvention = CallingConvention.Cdecl)]
    public static extern int CloseDevice();

    // 计算一次体积测量
    [DllImport("VolumeMeasurementDll.dll", EntryPoint = "ComputeOnce", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ComputeOnce();

    // 获取体积测量结果
    [DllImport("VolumeMeasurementDll.dll", EntryPoint = "GetDmsResult", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetDmsResult(
        [Out] float[] dimensionData,
        [Out] [MarshalAs(UnmanagedType.LPArray)] // Use MarshalAs like example, removed SizeConst
        byte[] imageData);

    // 获取体积测量结果错误信息
    [DllImport("VolumeMeasurementDll.dll", EntryPoint = "GetErrorMes", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetErrorMes([Out] byte[] errMes);

    // 获取系统状态
    [DllImport("VolumeMeasurementDll.dll", EntryPoint = "GetSystemState", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetSystemState([Out] int[] systemState);

    // 设置托盘高度
    [DllImport("VolumeMeasurementDll.dll", EntryPoint = "SetPalletHeight", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SetPalletHeight(int palletHeight);

    // 获取尺寸刻度图，参数verticalViewImage存储俯视图，参数sideViewImage用于存储侧视图
    [DllImport("VolumeMeasurementDll.dll", EntryPoint = "GetDimensionImage", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetDimensionImage(
        [Out] [MarshalAs(UnmanagedType.LPArray)] byte[] verticalViewImage, // Use MarshalAs like example
        [Out] [MarshalAs(UnmanagedType.LPArray)] byte[] sideViewImage); // Use MarshalAs like example
}
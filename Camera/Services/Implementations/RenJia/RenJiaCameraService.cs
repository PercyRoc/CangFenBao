using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Text;
using Common.Models.Package;
using Camera.Interface; // 从 DeviceService... 更改为 Camera.Interface
using Camera.Models;    // 从 DeviceService... 更改为 Camera.Models
using Serilog;
using System.Windows.Media.Imaging;
using System.IO;
using System.Diagnostics;

namespace Camera.Services.Implementations.RenJia; // 更改的命名空间

/// <summary>
///     测量结果
/// </summary>
public record MeasureResult(
    bool IsSuccess,
    string? ErrorMessage = null,
    float Length = 0,
    float Width = 0,
    float Height = 0,
    BitmapSource? MeasuredImage = null);

/// <summary>
///     尺寸刻度图结果
/// </summary>
public record DimensionImagesResult(
    bool IsSuccess,
    string? ErrorMessage = null,
    BitmapSource? VerticalViewImage = null, // 俯视图
    BitmapSource? SideViewImage = null, // 侧视图
    float Width = 0,
    float Height = 0,
    BitmapSource? MeasuredImage = null);

/// <summary>
///     人加体积相机服务
/// </summary>
public class RenJiaCameraService : ICameraService // 实现 ICameraService 接口
{
    private readonly Subject<(BitmapSource Image, string CameraId)> _imageSubject = new();
    private readonly Subject<PackageInfo> _packageSubject = new();
    private readonly CancellationTokenSource _processingCancellation = new();
    private bool _disposed;
    private bool _isConnected;

    public event Action<string?, bool>? ConnectionChanged; // string? 类型以匹配 ICameraService（如果该接口中定义为可空类型，此处假设是这样）

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (_isConnected == value) return;

            _isConnected = value;
            ConnectionChanged?.Invoke("RenJia_0", value); // 更改为使用 CameraId 调用事件
        }
    }

    public IObservable<PackageInfo> PackageStream => _packageSubject.AsObservable();

    // 此特定的 ImageStream 不是 ICameraService 接口的一部分，作为 RenJiaCameraService 的公共成员保留
    public IObservable<BitmapSource> ImageStream => _imageSubject.Select(tuple => tuple.Image);

    public IObservable<(BitmapSource Image, string CameraId)> ImageStreamWithId => _imageSubject.AsObservable();

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
            Stop(); //确保在初始化失败时调用 Stop
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动人加体积相机服务失败");
            return false;
        }
    }

    public bool Stop()
    {
        if (!IsConnected && !_disposed) // 同时检查 _disposed，如果已释放则无需停止
        {
           // 如果未连接且未释放，则可能部分启动资源已被获取。
           // 然而，当前的 Stop 逻辑主要处理活动连接。
           // 如果没有活动连接，若 StartProcess 已被调用，则 KillProcess 可能仍然相关。
            try
            {
                Log.Information("人加体积相机服务未连接，尝试清理后台进程（如果存在）。");
                NativeMethods.KillProcess(); // 即使未"连接"，也尝试终止进程
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "尝试关闭后台进程时出错（服务未连接）。");
            }
            return true; // 表明它已"停止"，因为它并未运行。
        }
        if (_disposed) return true; // 已释放


        try
        {
            Log.Information("正在停止人加体积相机服务...");

            // 1. 关闭设备
            var closeResult = NativeMethods.CloseDevice();
            if (closeResult != 0) Log.Warning("关闭设备失败：{Result}", closeResult);

            // 2. 关闭后台程序
            var success = NativeMethods.KillProcess();
            if (!success) Log.Warning("关闭后台程序失败");

            // 重置状态
            IsConnected = false;

            Log.Information("人加体积相机服务已停止");
            return success;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止人加体积相机服务时发生错误");
            IsConnected = false; // 强制重置状态
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; // 在开始时设置 disposed 为 true

        Log.Information("正在释放人加体积相机资源...");
        Stop(); // 确保在释放资源前停止服务

        try
        {
            _packageSubject.OnCompleted();
            _packageSubject.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放 PackageSubject 时发生错误");
        }

        try
        {
            _imageSubject.OnCompleted();
            _imageSubject.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放 ImageSubject 时发生错误");
        }
        
        try
        {
            _processingCancellation.Cancel();
            _processingCancellation.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放 CancellationTokenSource 时发生错误");
        }
        
        Log.Information("人加体积相机资源已释放。");
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     触发一次测量
    /// </summary>
    /// <returns>测量结果</returns>
    public MeasureResult TriggerMeasure()
    {
        if (!IsConnected)
        {
            Log.Warning("人加相机未连接，无法触发测量。");
            return new MeasureResult(false, "相机未连接");
        }
        if (_disposed)
        {
            Log.Warning("人加相机已释放，无法触发测量。");
            return new MeasureResult(false, "相机已释放");
        }

        try
        {
            var swTotal = Stopwatch.StartNew();

            var state = new int[1];
            var stateResult = NativeMethods.GetSystemState(state);
            if (stateResult != 0)
            {
                Log.Warning("获取系统状态失败：{Result}", stateResult);
                return new MeasureResult(false, $"获取系统状态失败: {stateResult}");
            }

            if (state[0] != 1) 
            {
                Log.Warning("设备状态异常，当前状态：{State}，依然尝试获取图像", state[0]);
            }

            var measureTask = Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    // 2. ComputeOnce
                    sw.Restart();
                    var computeResult = NativeMethods.ComputeOnce();
                    sw.Stop();
                    Log.Information("[RenJia] ComputeOnce耗时: {Elapsed}ms, Result={Result}", sw.ElapsedMilliseconds, computeResult);
                    if (computeResult != 0)
                    {
                        Log.Warning("触发测量失败：{Result}", computeResult);
                    }

                    // 3. GetDmsResult
                    sw.Restart();
                    var dimensionData = new float[3];
                    var imageDataBuffer = new byte[10 * 1024 * 1024]; // 10MB 缓冲区
                    var len = NativeMethods.GetDmsResult(dimensionData, imageDataBuffer);
                    sw.Stop();
                    Log.Information("[RenJia] GetDmsResult耗时: {Elapsed}ms, 长度: {Len}", sw.ElapsedMilliseconds, len);

                    BitmapSource? measuredBitmapImage = null; // 用于存储转换后的图像

                    if (len > 0)
                    {
                        // 4. 图像解码
                        var swImg = Stopwatch.StartNew();
                        try
                        {
                            using var memoryStream = new MemoryStream(imageDataBuffer, 0, len);
                            var bitmapImage = new BitmapImage();
                            bitmapImage.BeginInit();
                            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                            bitmapImage.StreamSource = memoryStream;
                            bitmapImage.EndInit();
                            bitmapImage.Freeze();
                            measuredBitmapImage = bitmapImage; // 存起来

                            _imageSubject.OnNext((bitmapImage, "RenJia_0"));
                            Log.Debug("推送测量图像 (JPEG, 大小: {Size} bytes)", len);
                        }
                        catch (NotSupportedException nex)
                        {
                            Log.Warning(nex, "转换测量图像数据失败，确认数据是否为有效的 JPEG 格式 (大小: {Size})", len);
                            var prefix = string.Empty;
                            const int prefixLength = 32;
                            try
                            {
                                prefix = Convert.ToBase64String(imageDataBuffer, 0, Math.Min(len, prefixLength));
                            }
                            catch {/* ignored */}
                            Log.Warning("Data Prefix (Base64): {Prefix}", prefix);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "处理测量图像数据时发生错误 (大小: {Size})", len);
                        }
                        finally
                        {
                            swImg.Stop();
                            Log.Information("[RenJia] 图像解码耗时: {Elapsed}ms", swImg.ElapsedMilliseconds);
                        }
                    }
                    else
                    {
                        var error = GetErrorMessage();
                        Log.Warning("获取测量结果失败：{Error}", error);
                        return new MeasureResult(false, error, MeasuredImage: null);
                    }

                    if (dimensionData.Any(static d => float.IsNaN(d) || float.IsInfinity(d) || d is <= 0 or > 2000))
                    {
                        var error = $"测量结果无效：L={dimensionData[0]}, W={dimensionData[1]}, H={dimensionData[2]}";
                        Log.Warning(error);
                        return new MeasureResult(false, error, MeasuredImage: null);
                    }

                    swTotal.Stop();
                    Log.Information("测量成功：{Length}x{Width}x{Height}mm，总耗时：{SwTotal}ms",
                        dimensionData[0], dimensionData[1], dimensionData[2], swTotal.ElapsedMilliseconds);

                    return new MeasureResult(
                        true,
                        Length: dimensionData[0], // dimensionData 单位是毫米
                        Width: dimensionData[1],
                        Height: dimensionData[2],
                        MeasuredImage: measuredBitmapImage
                    );
                }
                catch (AccessViolationException ex)
                {
                    Log.Fatal(ex, "测量过程中发生内存访问冲突");
                    return new MeasureResult(false, "硬件通信异常", MeasuredImage: null);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "测量过程中发生异常");
                    return new MeasureResult(false, ex.Message, MeasuredImage: null);
                }
            });
            return measureTask.Result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行测量时发生异常");
            return new MeasureResult(false, ex.Message, MeasuredImage: null);
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

    public int SetPalletHeight(int palletHeightMm)
    {
        if (!IsConnected || _disposed)
        {
            Log.Warning("相机未连接或已释放，无法设置托盘高度");
            return -1; 
        }
        try
        {
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

    public IEnumerable<CameraInfo> GetAvailableCameras() // 更改返回类型
    {
        if (IsConnected)
        {
            return new List<CameraInfo> // 更改为 Camera.Models.CameraInfo
            {
                new() { Id = "RenJia_0", Name = "人加体积相机" } 
            };
        }
        return Enumerable.Empty<CameraInfo>(); // 为清晰起见，使用 Enumerable.Empty<CameraInfo>()
    }

    public async Task<DimensionImagesResult> GetDimensionImagesAsync()
    {
        if (!IsConnected || _disposed)
        {
            Log.Warning("相机未连接或已释放，无法获取尺寸刻度图");
            return new DimensionImagesResult(false, "相机未连接或已释放");
        }

        try
        {
            return await Task.Run(() =>
            {
                const int bufferSize = 10 * 1024 * 1024; 
                var verticalViewBuffer = new byte[bufferSize];
                var sideViewBuffer = new byte[bufferSize];

                var result = NativeMethods.GetDimensionImage(verticalViewBuffer, sideViewBuffer);

                if (result != 0)
                {
                    var error = GetErrorMessage();
                    Log.Warning("获取尺寸刻度图失败：{Result} - {Error}", result, error);
                    return new DimensionImagesResult(false, $"获取图像失败: {error}");
                }

                BitmapSource? verticalImage = null;
                BitmapSource? sideImage = null;
                string? conversionError = null;

                try
                {
                    verticalImage = ConvertBytesToBitmapSource(verticalViewBuffer, "俯视图");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理或转换俯视图图像失败");
                    conversionError = "转换俯视图失败";
                }

                try
                {
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
                else
                {
                    return new DimensionImagesResult(false, conversionError ?? "无法转换任何图像");
                }
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
        // 1. 更健壮的大小检测
        int imageSize;
        try
        {
            imageSize = BitConverter.ToInt32(buffer, 0);
            // 添加大小合理性检查
            if (imageSize <= 0 || imageSize > buffer.Length - 4)
            {
                Log.Warning("{ImageName} 图像大小无效: {Size}，尝试检测实际图像大小", 
                            imageNameForLogging, imageSize);
                
                // 尝试通过查找图像结束标记确定实际大小
                imageSize = FindActualImageSize(buffer, 4);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{ImageName} 解析图像大小时出错", imageNameForLogging);
            imageSize = buffer.Length - 4; // 使用最大可能大小
        }

        // 2. 使用更安全的解码方式
        try
        {
            // 仅使用有效范围内的数据
            var validData = buffer.Skip(4).Take(imageSize).ToArray();
            
            using var stream = new MemoryStream(validData);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{ImageName} 图像解码失败", imageNameForLogging);
            return null;
        }
    }

    // 新增辅助方法：通过查找JPEG/PNG结束标记确定实际图像大小
    private static int FindActualImageSize(byte[] buffer, int startIndex)
    {
        // JPEG结束标记: 0xFF, 0xD9
        for (int i = startIndex; i < buffer.Length - 1; i++)
        {
            if (buffer[i] == 0xFF && buffer[i + 1] == 0xD9)
                return i - startIndex + 2; // 包含结束标记
        }
        
        // PNG结束标记: IEND块
        byte[] pngEnd = [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];
        for (int i = startIndex; i < buffer.Length - pngEnd.Length; i++)
        {
            if (buffer.Skip(i).Take(pngEnd.Length).SequenceEqual(pngEnd))
                return i - startIndex + pngEnd.Length;
        }
        
        // 未找到结束标记，返回最大可能大小
        return buffer.Length - startIndex;
    }
    
    // 此重载是人加相机特有的，并非 ICameraService 接口的一部分。
    // 它调用另一个 TriggerMeasure 方法并适配返回类型。
    public (bool isSuccess, float length, float width, float height, string? errorMessage) TriggerMeasure(
      int workMode = 7, int timeoutMs = 3000)
    {
        var result = TriggerMeasure(); 
        return (result.IsSuccess, result.Length, result.Width, result.Height, result.ErrorMessage);
    }
}

/// <summary>
///     人加体积相机原生方法
/// </summary>
internal static class NativeMethods
{
    private const string DllName = "VolumeMeasurementDll.dll";

    [DllImport(DllName, EntryPoint = "KillProcess", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool KillProcess();

    [DllImport(DllName, EntryPoint = "StartProcess", CallingConvention = CallingConvention.Cdecl)]
    public static extern void StartProcess();

    [DllImport(DllName, EntryPoint = "ScanDevice", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ScanDevice();

    [DllImport(DllName, EntryPoint = "OpenDevice", CallingConvention = CallingConvention.Cdecl)]
    public static extern int OpenDevice();

    [DllImport(DllName, EntryPoint = "CloseDevice", CallingConvention = CallingConvention.Cdecl)]
    public static extern int CloseDevice();

    [DllImport(DllName, EntryPoint = "ComputeOnce", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ComputeOnce();

    [DllImport(DllName, EntryPoint = "GetDmsResult", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetDmsResult(
        [Out] float[] dimensionData,
        [Out, MarshalAs(UnmanagedType.LPArray)] // 使用 MarshalAs (如示例)，移除了 SizeConst
        byte[] imageData);

    [DllImport(DllName, EntryPoint = "GetErrorMes", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetErrorMes([Out] byte[] errMes);

    [DllImport(DllName, EntryPoint = "GetSystemState", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetSystemState([Out] int[] systemState);

    [DllImport(DllName, EntryPoint = "SetPalletHeight", CallingConvention = CallingConvention.Cdecl)]
    public static extern int SetPalletHeight(int palletHeight);

    [DllImport(DllName, EntryPoint = "GetDimensionImage", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetDimensionImage(
        [Out, MarshalAs(UnmanagedType.LPArray)] // 使用 MarshalAs (如示例)
        byte[] verticalViewImage, 
        [Out, MarshalAs(UnmanagedType.LPArray)] // 使用 MarshalAs (如示例)
        byte[] sideViewImage); 
} 
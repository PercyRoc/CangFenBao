using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using CommonLibrary.Models;
using CommonLibrary.Models.Settings.Camera;
using DeviceService.Camera.Models;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DeviceService.Camera.RenJia;

/// <summary>
/// 测量结果
/// </summary>
public record MeasureResult(
    bool IsSuccess,
    string? ErrorMessage = null,
    float Length = 0,
    float Width = 0,
    float Height = 0);

/// <summary>
///     人加体积相机服务
/// </summary>
public class RenJiaCameraService : ICameraService
{
    private readonly Subject<PackageInfo> _packageSubject = new();
    private readonly Subject<(Image<Rgba32> image, IReadOnlyList<BarcodeLocation> barcodes)> _imageSubject = new();
    private readonly CancellationTokenSource _processingCancellation = new();
    private bool _disposed;
    private bool _isConnected;
    private VolumeSettings? _settings;

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

    public IObservable<PackageInfo> PackageStream => _packageSubject.AsObservable();

    public IObservable<(Image<Rgba32> image, IReadOnlyList<BarcodeLocation> barcodes)> ImageStream =>
        _imageSubject.AsObservable();

    public bool Start()
    {
        try
        {
            Log.Information("开始启动人加体积相机服务...");

            // 1. 关闭可能存在的后台程序
            if (!NativeMethods.KillProcess())
            {
                Log.Warning("关闭已存在的后台程序失败");
            }
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
            IsConnected = true;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动人加体积相机服务失败");
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            Log.Information("正在停止人加体积相机服务...");

            // 1. 关闭设备
            var closeResult = NativeMethods.CloseDevice();
            if (closeResult != 0)
            {
                Log.Warning("关闭设备失败：{Result}", closeResult);
            }

            // 2. 关闭后台程序
            if (!NativeMethods.KillProcess())
            {
                Log.Warning("关闭后台程序失败");
            }

            IsConnected = false;
            Log.Information("人加体积相机服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止人加体积相机服务时发生错误");
        }
    }

    public IEnumerable<DeviceCameraInfo>? GetCameraInfos()
    {
        try
        {
            var deviceNum = NativeMethods.ScanDevice();
            if (deviceNum <= 0)
            {
                Log.Warning("未发现人加体积相机设备");
                return null;
            }

            var cameras = new List<DeviceCameraInfo>();
            for (var i = 0; i < deviceNum; i++)
            {
                cameras.Add(new DeviceCameraInfo
                {
                    SerialNumber = $"RenJia_{i}",
                    Model = "RenJia Volume Camera",
                    IpAddress = "USB",
                    MacAddress = $"RenJia_{i}"
                });
            }

            return cameras;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取人加体积相机列表失败");
            return null;
        }
    }

    public void UpdateConfiguration(CameraSettings config)
    {
        if (config is not VolumeSettings volumeSettings)
        {
            Log.Warning("配置类型错误，期望 VolumeSettings 类型");
            return;
        }
        
        try
        {
            _settings = volumeSettings;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新人加体积相机配置失败");
        }
    }

    /// <summary>
    /// 触发一次测量
    /// </summary>
    /// <returns>测量结果</returns>
    public MeasureResult TriggerMeasure()
    {
        try
        {
            var startTime = DateTime.Now;
            var timeoutMs = _settings?.TimeoutMs ?? 500;

            // 创建测量任务
            var measureTask = Task.Run(() =>
            {
                try
                {
                    // 1. 执行测量（非阻塞）
                    var computeResult = NativeMethods.ComputeOnce();
                    if (computeResult != 0)
                    {
                        Log.Warning("触发测量失败：{Result}", computeResult);
                        return new MeasureResult(false, "触发测量失败");
                    }

                    // 等待设备准备就绪
                    Thread.Sleep(50);

                    // 检查设备状态
                    var state = new int[1];
                    NativeMethods.GetSystemState(state);

                    // 2. 获取测量结果
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
                    if (dimensionData.Any(d => float.IsNaN(d) || float.IsInfinity(d) || d <= 0 || d > 2000))
                    {
                        var error = $"测量结果无效：L={dimensionData[0]}, W={dimensionData[1]}, H={dimensionData[2]}";
                        Log.Warning(error);
                        return new MeasureResult(false, error);
                    }

                    // 4. 处理图像数据
                    try
                    {
                        var imageData = new byte[len];
                        Array.Copy(imageDataBuffer, imageData, len);
                        
                        using var image = Image.Load<Rgba32>(imageData);
                        _imageSubject.OnNext((image.Clone(), new List<BarcodeLocation>()));
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "处理图像数据时发生错误");
                        // 图像处理失败不影响尺寸测量结果
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

            // 等待测量任务完成或超时
            if (measureTask.Wait(timeoutMs)) return measureTask.Result;
            Log.Warning("体积测量操作超时（{Timeout}ms）", timeoutMs);

            // 在后台继续等待测量任务完成，避免资源泄漏
            _ = measureTask.ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Log.Error(t.Exception, "超时后的测量任务发生异常");
            }, TaskScheduler.Default);

            return new MeasureResult(false, "测量操作超时");

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
            return messageLength <= 0 ? "未知错误" : System.Text.Encoding.UTF8.GetString(errorMessage, 0, messageLength).TrimEnd('\0');
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取错误信息时发生错误");
            return "获取错误信息失败";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            Stop();
            _packageSubject.Dispose();
            _imageSubject.Dispose();
            _processingCancellation.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放人加体积相机资源时发生错误");
        }
        finally
        {
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
///     人加体积相机原生方法
/// </summary>
internal static partial class NativeMethods
{
    // 关闭后台应用程序
    [LibraryImport("VolumeMeasurementDll.dll", EntryPoint = "KillProcess")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillProcess();

    // 开启后台应用程序
    [LibraryImport("VolumeMeasurementDll.dll", EntryPoint = "StartProcess")]
    public static partial void StartProcess();

    // 扫描设备，返回在线设备数量
    [LibraryImport("VolumeMeasurementDll.dll", EntryPoint = "ScanDevice")]
    public static partial int ScanDevice();

    // 开启设备
    [LibraryImport("VolumeMeasurementDll.dll", EntryPoint = "OpenDevice")]
    public static partial int OpenDevice();

    // 关闭设备
    [LibraryImport("VolumeMeasurementDll.dll", EntryPoint = "CloseDevice")]
    public static partial int CloseDevice();

    // 计算一次体积测量
    [LibraryImport("VolumeMeasurementDll.dll", EntryPoint = "ComputeOnce")]
    public static partial int ComputeOnce();

    // 获取体积测量结果
    [LibraryImport("VolumeMeasurementDll.dll", EntryPoint = "GetDmsResult")]
    public static partial int GetDmsResult(
        [Out] float[] dimensionData,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 10485760)] byte[] imageData);

    // 获取体积测量结果错误信息
    [LibraryImport("VolumeMeasurementDll.dll", EntryPoint = "GetErrorMes")]
    public static partial int GetErrorMes([Out] byte[] errMes);

    // 获取测量时刻的图像信息
    [LibraryImport("VolumeMeasurementDll.dll", EntryPoint = "GetMeasureImageFromId")]
    public static partial int GetMeasureImageFromId(
        IntPtr imageData,
        int cameraId);

    // 获取系统状态
    [LibraryImport("VolumeMeasurementDll.dll", EntryPoint = "GetSystemState")]
    public static partial int GetSystemState([Out] int[] systemState);
} 
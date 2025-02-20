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
    float Height = 0,
    string? ImageId = null);

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

    public event Action<string, bool>? ConnectionChanged;

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
            Log.Information("发现 {Count} 台人加体积相机", deviceNum);

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
        ArgumentNullException.ThrowIfNull(config);
        
        try
        {
            Log.Information("更新人加体积相机配置");
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
            // 1. 获取系统状态
            var systemState = new int[1];
            var stateResult = NativeMethods.GetSystemState(systemState);
            if (stateResult != 0)
            {
                var error = "获取系统状态失败";
                Log.Warning(error + "：{Result}", stateResult);
                return new MeasureResult(false, error);
            }

            // 2. 检查系统是否空闲
            if (systemState[0] != 0)
            {
                var error = "系统当前不是空闲状态";
                Log.Warning(error + "：{State}", systemState[0]);
                return new MeasureResult(false, error);
            }

            // 3. 触发测量
            var computeResult = NativeMethods.ComputeOnce();
            if (computeResult != 0)
            {
                var error = "触发测量失败";
                Log.Warning(error + "：{Result}", computeResult);
                return new MeasureResult(false, error);
            }

            // 4. 等待测量完成
            var waitStart = DateTime.Now;
            while (DateTime.Now - waitStart < TimeSpan.FromSeconds(5))
            {
                stateResult = NativeMethods.GetSystemState(systemState);
                if (stateResult != 0)
                {
                    var error = "等待测量完成时获取系统状态失败";
                    Log.Warning(error + "：{Result}", stateResult);
                    return new MeasureResult(false, error);
                }

                if (systemState[0] != 2) continue;
                
                // 获取测量结果
                var dimensionData = new float[3]; // 存储长、宽、高数据
                var len = NativeMethods.GetDmsResult(dimensionData, []);  // 只获取尺寸数据

                if (len <= 0)
                {
                    var error = "获取测量结果失败";
                    Log.Warning(error);
                    return new MeasureResult(false, error);
                }

                // 检查测量结果是否有效
                if (dimensionData[0] == 0 || dimensionData[1] == 0)
                {
                    var error = GetErrorMessage();
                    Log.Warning("测量结果无效：{Error}", error);
                    return new MeasureResult(false, error);
                }

                Log.Debug("测量完成：L={Length}, W={Width}, H={Height}",
                    dimensionData[0], dimensionData[1], dimensionData[2]);

                return new MeasureResult(
                    true,
                    Length: dimensionData[0],
                    Width: dimensionData[1],
                    Height: dimensionData[2],
                    ImageId: "0" // 使用第一个相机
                );
            }

            return new MeasureResult(false, "测量超时");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "触发测量时发生错误");
            return new MeasureResult(false, ex.Message);
        }
    }

    /// <summary>
    /// 根据图像ID获取测量图像
    /// </summary>
    /// <param name="imageId">图像ID</param>
    /// <returns>图像数据</returns>
    public byte[]? GetMeasureImageFromId(string? imageId)
    {
        if (string.IsNullOrEmpty(imageId))
        {
            Log.Warning("图像ID为空");
            return null;
        }

        try
        {
            var measureImageData = new byte[5120000];
            var imageLen = NativeMethods.GetMeasureImageFromId(measureImageData, int.Parse(imageId));
            if (imageLen <= 0)
            {
                Log.Warning("获取测量图像失败");
                return null;
            }

            var result = new byte[imageLen];
            Array.Copy(measureImageData, result, imageLen);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取测量图像时发生错误");
            return null;
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

    private static async Task<Image<Rgba32>?> CreateImageFromData(byte[] imageData, int length)
    {
        try
        {
            using var memoryStream = new MemoryStream(imageData, 0, length);
            return await Image.LoadAsync<Rgba32>(memoryStream);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "创建图像时发生错误");
            return null;
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
    public static partial int GetDmsResult([Out] float[] dimensionData, [Out] byte[] imageData);

    // 获取体积测量结果错误信息
    [LibraryImport("VolumeMeasurementDll.dll", EntryPoint = "GetErrorMes")]
    public static partial int GetErrorMes([Out] byte[] errMes);

    // 获取测量时刻的图像信息
    [LibraryImport("VolumeMeasurementDll.dll", EntryPoint = "GetMeasureImageFromId")]
    public static partial int GetMeasureImageFromId([Out] byte[] imageData, int cameraId);

    // 获取系统状态
    [LibraryImport("VolumeMeasurementDll.dll", EntryPoint = "GetSystemState")]
    public static partial int GetSystemState([Out] int[] systemState);
} 
using Common.Models.Package;
using DeviceService.DataSourceDevices.Camera.Models;
using MvCamCtrl.NET;
using Serilog;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeviceService.DataSourceDevices.Camera.Hikvision;

/// <summary>
/// 使用海康工业相机 SDK 实现的相机服务
/// </summary>
public sealed class HikvisionIndustrialCameraService : ICameraService
{
    private readonly Subject<PackageInfo> _packageSubject = new();
    private readonly Subject<(BitmapSource Image, string CameraId)> _imageSubject = new();
    private bool _disposedValue;

    // 海康工业相机 SDK 相关字段
    private MyCamera? _device; // 当前连接的相机设备
    private MyCamera.MV_CC_DEVICE_INFO_LIST _deviceList; // 枚举的设备列表
    private string? _currentDeviceSerialNumber; // 当前连接设备的序列号
    private Thread? _grabThread; // 图像采集的线程
    private bool _grabbing; // 是否正在采集图像
    private readonly object _lockObj = new(); // 线程同步锁

    // 用于从驱动获取图像的缓存
    private IntPtr _bufForDriver = IntPtr.Zero;
    private static readonly object BufForDriverLock = new();

    // 用于图像格式转换的缓存
    private IntPtr _convertDstBuf = IntPtr.Zero;

    // 记录SDK是否已初始化
    private static bool _sdkInitialized;

    public bool IsConnected { get; private set; }

    public IObservable<PackageInfo> PackageStream => _packageSubject;

    public IObservable<BitmapSource> ImageStream => _imageSubject.Select(tuple => tuple.Image);

    public IObservable<(BitmapSource Image, string CameraId)> ImageStreamWithId => _imageSubject.AsObservable();

    public event Action<string?, bool>? ConnectionChanged;

    // 构造函数中初始化SDK
    public HikvisionIndustrialCameraService()
    {
        // 确保SDK只初始化一次
        lock (_lockObj)
        {
            if (_sdkInitialized) return;
            var nRet = MyCamera.MV_CC_Initialize_NET();
            if (nRet != 0)
            {
                Log.Error("初始化海康工业相机 SDK 失败: {ErrorCode}", nRet);
            }
            else
            {
                _sdkInitialized = true;
                Log.Information("海康工业相机 SDK 初始化成功");
            }
        }
    }

    public bool Start()
    {
        if (IsConnected)
        {
            return true; // 已经连接，直接返回成功
        }

        try
        {
            // 枚举设备
            if (!EnumerateDevices())
            {
                return false;
            }

            // 如果没有找到设备，返回失败
            if (_deviceList.nDeviceNum <= 0)
            {
                Log.Warning("未找到海康工业相机设备");
                return false;
            }

            // 默认选择第一个设备并连接
            if (!ConnectToDevice(0))
            {
                return false;
            }

            // 设置连续采集模式
            _device?.MV_CC_SetEnumValue_NET("AcquisitionMode",
                (uint)MyCamera.MV_CAM_ACQUISITION_MODE.MV_ACQ_MODE_CONTINUOUS);
            _device?.MV_CC_SetEnumValue_NET("TriggerMode", (uint)MyCamera.MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);

            // 开始图像采集
            if (StartGrabbing()) return true;

            // 如果启动采集失败，断开设备连接
            DisconnectFromDevice();
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动海康工业相机服务失败");
            Stop(); // 确保清理资源
            return false;
        }
    }

    public bool Stop()
    {
        if (!IsConnected)
        {
            return true; // 已经断开连接，直接返回成功
        }

        try
        {
            // 停止图像采集
            StopGrabbing();

            // 断开设备连接
            DisconnectFromDevice();

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止海康工业相机服务失败");
            return false;
        }
    }

    public IEnumerable<CameraBasicInfo> GetAvailableCameras()
    {
        var availableCameras = new List<CameraBasicInfo>();
        var deviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
        var nRet = MyCamera.MV_CC_EnumDevices_NET(
            MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE,
            ref deviceList);

        if (nRet != 0)
        {
            Log.Error("枚举海康设备失败: {ErrorCode}", nRet);
            return availableCameras; // 返回空列表
        }

        for (var i = 0; i < deviceList.nDeviceNum; i++)
        {
            try
            {
                var deviceInfo = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(
                    deviceList.pDeviceInfo[i],
                    typeof(MyCamera.MV_CC_DEVICE_INFO))!;

                var serialNumber = GetDeviceSerialNumber(deviceInfo);
                var modelName = GetDeviceModelName(deviceInfo);
                var name = string.IsNullOrEmpty(modelName) ? $"海康相机 {i + 1}" : $"{modelName} ({serialNumber})";

                availableCameras.Add(new CameraBasicInfo
                {
                    Id = serialNumber, // 使用序列号作为唯一ID
                    Name = name,
                    Model = modelName,
                    SerialNumber = serialNumber
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理设备信息时出错，索引: {Index}", i);
            }
        }

        return availableCameras;
    }

    #region 内部私有方法

    private bool EnumerateDevices()
    {
        // 清理之前的设备列表
        _deviceList.nDeviceNum = 0;

        // 枚举所有类型的设备
        var nRet = MyCamera.MV_CC_EnumDevices_NET(
            MyCamera.MV_GIGE_DEVICE |
            MyCamera.MV_USB_DEVICE,
            ref _deviceList);

        if (nRet != 0)
        {
            Log.Error("枚举海康工业相机设备失败: {ErrorCode}", nRet);
            return false;
        }

        Log.Information("找到 {DeviceCount} 个海康工业相机设备", _deviceList.nDeviceNum);
        return true;
    }

    private bool ConnectToDevice(int deviceIndex)
    {
        if (deviceIndex < 0 || deviceIndex >= _deviceList.nDeviceNum)
        {
            Log.Error("设备索引 {Index} 超出范围 (0-{MaxIndex})", deviceIndex, _deviceList.nDeviceNum - 1);
            return false;
        }

        // 创建设备实例
        _device = new MyCamera();

        // 获取设备信息
        var deviceInfo = (MyCamera.MV_CC_DEVICE_INFO)Marshal.PtrToStructure(
            _deviceList.pDeviceInfo[deviceIndex],
            typeof(MyCamera.MV_CC_DEVICE_INFO))!;

        // 创建设备
        var nRet = _device.MV_CC_CreateDevice_NET(ref deviceInfo);
        if (nRet != 0)
        {
            Log.Error("创建设备句柄失败: {ErrorCode}", nRet);
            return false;
        }

        // 打开设备
        nRet = _device.MV_CC_OpenDevice_NET();
        if (nRet != 0)
        {
            Log.Error("打开设备失败: {ErrorCode}", nRet);
            _device.MV_CC_DestroyDevice_NET();
            _device = null;
            return false;
        }

        // 如果是GigE相机，探测网络最佳包大小
        if (deviceInfo.nTLayerType == MyCamera.MV_GIGE_DEVICE)
        {
            var nPacketSize = _device.MV_CC_GetOptimalPacketSize_NET();
            if (nPacketSize > 0)
            {
                nRet = _device.MV_CC_SetIntValueEx_NET("GevSCPSPacketSize", nPacketSize);
                if (nRet != 0)
                {
                    Log.Warning("设置最佳包大小失败: {ErrorCode}", nRet);
                }
            }
        }

        // 获取设备的序列号
        _currentDeviceSerialNumber = GetDeviceSerialNumber(deviceInfo);

        // 设置为连接状态
        IsConnected = true;
        RaiseConnectionChanged(_currentDeviceSerialNumber, true);

        Log.Information("已连接到海康工业相机: {SerialNumber}", _currentDeviceSerialNumber);
        return true;
    }

    private void DisconnectFromDevice()
    {
        if (_device == null) return;
        try
        {
            // 关闭设备
            Log.Debug("正在关闭设备...");
            var nRet = _device.MV_CC_CloseDevice_NET();
            if (nRet != 0)
            {
                Log.Error("关闭设备失败: {ErrorCode}", nRet);
            }
            else
            {
                Log.Debug("设备已关闭");
            }

            // 销毁设备
            Log.Debug("正在销毁设备句柄...");
            nRet = _device.MV_CC_DestroyDevice_NET();
            if (nRet != 0)
            {
                Log.Error("销毁设备失败: {ErrorCode}", nRet);
            }
            else
            {
                Log.Debug("设备句柄已销毁");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "断开设备连接异常");
        }
        finally
        {
            _device = null;
            // 设置为断开状态
            IsConnected = false; // 这会隐式调用 RaiseConnectionChanged
            // RaiseConnectionChanged(_currentDeviceSerialNumber, false); // 不再需要显式调用
            Log.Information("设备已断开连接");
            _currentDeviceSerialNumber = null;
        }
    }

    private bool StartGrabbing()
    {
        if (_device == null || !IsConnected || _grabbing)
        {
            return false;
        }

        try
        {
            // 1. 先开始采集
            var nRet = _device.MV_CC_StartGrabbing_NET();
            if (nRet != 0)
            {
                _grabbing = false; // 确保标志位为 false
                // 线程此时还未创建或启动，无需 Join
                Log.Error("开始采集图像失败: {ErrorCode}", nRet);
                return false;
            }

            // 采集成功后立即记录日志
            Log.Information("开始采集图像");

            // 2. 设置标志位
            _grabbing = true;

            // 3. 创建并启动图像采集线程
            _grabThread = new Thread(GrabThreadProcess);
            _grabThread.Start(_device);

            // 可选：短暂等待或检查线程状态，但通常先启动采集再启动线程就够了
            // Thread.Sleep(50); 

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动图像采集失败");
            // 如果 StartGrabbing 成功但后续出错，需要尝试停止
            if (_grabbing) // 检查标志位，判断是否需要停止
            {
                try
                {
                    _device?.MV_CC_StopGrabbing_NET();
                    Log.Warning("因启动采集线程异常，已尝试停止采集");
                }
                catch (Exception stopEx)
                {
                    Log.Error(stopEx, "尝试停止采集时发生额外错误");
                }
            }

            _grabbing = false; // 确保最终标志位为 false
            _grabThread = null; // 清理线程引用
            return false;
        }
    }

    private void StopGrabbing()
    {
        if (!_grabbing)
        {
            return;
        }

        try
        {
            // 1. 先停止SDK采集
            if (_device != null && IsConnected) // 检查设备是否有效
            {
                var nRet = _device.MV_CC_StopGrabbing_NET();
                if (nRet != 0)
                {
                    Log.Error("停止采集图像失败: {ErrorCode}", nRet);
                    // 即使停止失败，也应继续尝试清理线程
                }
                else
                {
                    Log.Information("已发送停止采集命令");
                }
            }
            else
            {
                Log.Warning("尝试停止采集时设备无效或未连接");
            }

            // 2. 标志位设为false，通知线程退出循环
            _grabbing = false;

            // 3. 等待采集线程结束
            if (_grabThread is { IsAlive: true })
            {
                Log.Debug("等待图像采集线程退出...");
                if (!_grabThread.Join(2000)) // 最多等待2秒
                {
                    Log.Warning("图像采集线程未能在预期时间内退出");
                    // 可以考虑更强制的措施，但通常 Join 失败意味着线程可能卡死
                }
                else
                {
                    Log.Debug("图像采集线程已退出");
                }

                _grabThread = null; // 清理线程引用
            }

            // 4. 释放缓冲区 (如果还在使用的话 - 根据之前的分析，这些可能未使用或管理不当)
            lock (BufForDriverLock) // 如果 BufForDriverLock 确实未使用，此锁和内部代码也应移除
            {
                if (_bufForDriver != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_bufForDriver);
                    _bufForDriver = IntPtr.Zero;
                    Log.Debug("驱动图像缓冲区已释放");
                }
            }

            if (_convertDstBuf != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_convertDstBuf);
                _convertDstBuf = IntPtr.Zero;
                Log.Debug("转换目标缓冲区已释放");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止图像采集异常");
        }
    }

    // 图像采集线程函数
    private void GrabThreadProcess(object? deviceObj)
    {
        if (deviceObj is not MyCamera camera)
        {
            Log.Error("无效的相机对象传入图像采集线程");
            return;
        }

        // 图像信息结构体
        var stFrameInfo = new MyCamera.MV_FRAME_OUT();
        var pImageBuf = IntPtr.Zero;
        uint nBufSize = 0;

        try
        {
            // 主循环
            while (_grabbing)
            {
                // 从驱动获取一帧图像数据
                var nRet = camera.MV_CC_GetImageBuffer_NET(ref stFrameInfo, 1000);
                if (nRet == MyCamera.MV_OK)
                {
                    try
                    {
                        // 确保缓冲区足够大
                        if (pImageBuf == IntPtr.Zero || nBufSize < stFrameInfo.stFrameInfo.nFrameLen)
                        {
                            if (pImageBuf != IntPtr.Zero)
                            {
                                Marshal.FreeHGlobal(pImageBuf);
                            }

                            pImageBuf = Marshal.AllocHGlobal((int)stFrameInfo.stFrameInfo.nFrameLen);
                            nBufSize = stFrameInfo.stFrameInfo.nFrameLen;
                        }
                        try
                        {
                            var bitmapSource = ConvertToBitmapSource(stFrameInfo);
                            if (bitmapSource != null)
                            {
                                // 发布图像，包含相机ID
                                if (!string.IsNullOrEmpty(_currentDeviceSerialNumber))
                                {
                                    _imageSubject.OnNext((bitmapSource, _currentDeviceSerialNumber));
                                }
                                else
                                {
                                    Log.Warning("无法发布图像 {FrameNum}，因为当前设备序列号未知", stFrameInfo.stFrameInfo.nFrameNum);
                                }
                            }
                            else
                            {
                                Log.Warning("图像帧 {FrameNum} 转换失败，无法发布", stFrameInfo.stFrameInfo.nFrameNum);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "图像格式转换异常");
                        }
                    }
                    finally
                    {
                        // 释放图像缓冲
                        camera.MV_CC_FreeImageBuffer_NET(ref stFrameInfo);
                    }
                }
                else if (nRet != MyCamera.MV_E_NODATA)
                {
                    Log.Warning("获取图像失败: {ErrorCode}", nRet);
                    Thread.Sleep(50);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "图像采集线程异常");
        }
        finally
        {
            // 释放资源
            if (pImageBuf != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pImageBuf);
            }

            Log.Information("图像采集线程退出");
        }
    }

    // 将相机图像转换为BitmapSource格式
    private BitmapSource? ConvertToBitmapSource(MyCamera.MV_FRAME_OUT frameInfo)
    {
        if (_device == null) return null;

        try
        {
            // 准备转换参数
            var width = frameInfo.stFrameInfo.nWidth;
            var height = frameInfo.stFrameInfo.nHeight;

            // 分配转换缓冲区
            var rgbSize = (uint)(width * height * 3); // RGB每像素3字节
            var rgbBuffer = Marshal.AllocHGlobal((int)rgbSize);

            try
            {
                // 转换为RGB格式
                var convertParam = new MyCamera.MV_PIXEL_CONVERT_PARAM
                {
                    nWidth = width,
                    nHeight = height,
                    enSrcPixelType = frameInfo.stFrameInfo.enPixelType,
                    pSrcData = frameInfo.pBufAddr,
                    nSrcDataLen = frameInfo.stFrameInfo.nFrameLen,
                    enDstPixelType = MyCamera.MvGvspPixelType.PixelType_Gvsp_RGB8_Packed,
                    pDstBuffer = rgbBuffer,
                    nDstBufferSize = rgbSize
                };

                var nRet = _device.MV_CC_ConvertPixelType_NET(ref convertParam);
                if (nRet != 0)
                {
                    Log.Error("像素格式转换失败: {ErrorCode}", nRet);
                    return null;
                }


                // 创建BitmapSource
                var stride = width * 3; // RGB每像素3字节
                var pixelData = new byte[rgbSize];
                Marshal.Copy(rgbBuffer, pixelData, 0, (int)rgbSize);

                var bitmap = BitmapSource.Create(
                    width,
                    height,
                    96, 96, // DPI
                    PixelFormats.Rgb24,
                    null,
                    pixelData,
                    stride);

                bitmap.Freeze(); // 使图像可以跨线程访问
                return bitmap;
            }
            finally
            {
                Marshal.FreeHGlobal(rgbBuffer);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "图像转换异常");
            return null;
        }
    }

    private static string GetDeviceSerialNumber(MyCamera.MV_CC_DEVICE_INFO deviceInfo)
    {
        var serialNumber = "Unknown";

        switch (deviceInfo.nTLayerType)
        {
            // 根据设备类型获取序列号
            case MyCamera.MV_GIGE_DEVICE:
            {
                var gigeInfo = (MyCamera.MV_GIGE_DEVICE_INFO_EX)MyCamera.ByteToStruct(
                    deviceInfo.SpecialInfo.stGigEInfo,
                    typeof(MyCamera.MV_GIGE_DEVICE_INFO_EX));

                // 假设 chSerialNumber 是字符数组
                serialNumber = new string(gigeInfo.chSerialNumber).TrimEnd('\0');
                break;
            }
            case MyCamera.MV_USB_DEVICE:
            {
                var usbInfo = (MyCamera.MV_USB3_DEVICE_INFO_EX)MyCamera.ByteToStruct(
                    deviceInfo.SpecialInfo.stUsb3VInfo,
                    typeof(MyCamera.MV_USB3_DEVICE_INFO_EX));

                // 假设 chSerialNumber 是字符数组
                serialNumber = new string(usbInfo.chSerialNumber).TrimEnd('\0');
                break;
            }
        }
        // 可以根据需要添加其他设备类型的处理

        return serialNumber;
    }

    private void RaiseConnectionChanged(string? identifier, bool isConnected)
    {
        IsConnected = isConnected;
        ConnectionChanged?.Invoke(identifier, isConnected);
    }

    // 辅助方法获取设备型号名称
    private static string GetDeviceModelName(MyCamera.MV_CC_DEVICE_INFO deviceInfo)
    {
        string modelName = string.Empty;
        switch (deviceInfo.nTLayerType)
        {
            case MyCamera.MV_GIGE_DEVICE:
            {
                var gigeInfo = (MyCamera.MV_GIGE_DEVICE_INFO_EX)MyCamera.ByteToStruct(
                    deviceInfo.SpecialInfo.stGigEInfo,
                    typeof(MyCamera.MV_GIGE_DEVICE_INFO_EX));
                modelName = new string(gigeInfo.chModelName).TrimEnd('\0');
                break;
            }
            case MyCamera.MV_USB_DEVICE:
            {
                var usbInfo = (MyCamera.MV_USB3_DEVICE_INFO_EX)MyCamera.ByteToStruct(
                    deviceInfo.SpecialInfo.stUsb3VInfo,
                    typeof(MyCamera.MV_USB3_DEVICE_INFO_EX));
                modelName = new string(usbInfo.chModelName).TrimEnd('\0');
                break;
            }
        }

        return modelName;
    }

    #endregion

    // --- IDisposable 实现 ---

    private void Dispose(bool disposing)
    {
        if (_disposedValue) return;
        if (disposing)
        {
            // 释放托管状态(托管对象)
            Stop(); // 确保停止服务和断开设备连接
            _packageSubject.Dispose();
            _imageSubject.Dispose();
        }

        // 释放未托管的资源(未托管的对象)
        // 海康相机 SDK 资源已在 Stop() 中释放

        _disposedValue = true;

        // 最后一个实例释放时，执行SDK反初始化
        lock (_lockObj)
        {
            if (!_sdkInitialized) return;
            MyCamera.MV_CC_Finalize_NET();
            _sdkInitialized = false;
            Log.Information("海康工业相机 SDK 已反初始化");
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
    }
}
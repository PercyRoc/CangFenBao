using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Windows.Media; // Required for PixelFormats
using System.Windows.Media.Imaging;
using Common.Models.Package;
using DeviceService.DataSourceDevices.Camera.Models;
using MvCodeReaderSDKNet;
using Serilog;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Text; // Required for Encoding


namespace DeviceService.DataSourceDevices.Camera.HikvisionSmartSdk;

/// <summary>
/// 海康威视智能相机服务实现。
/// </summary>
public class HikvisionSmartCameraService : ICameraService
{
    // 使用 ConcurrentDictionary 管理多个设备及其线程
    private readonly ConcurrentDictionary<string, (MvCodeReader device, Thread thread)> _activeDevices = new();

    private MvCodeReader.MV_CODEREADER_DEVICE_INFO_LIST _deviceList; // 设备列表

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _deviceCts = new();

    private readonly object _lock = new();

    // private HikvisionSmartCameraSettings? _cameraSettings; // 可能仍需要通用设置
    private readonly Subject<PackageInfo> _packageSubject = new();
    private readonly Subject<BitmapSource> _imageSubject = new();

    private readonly Subject<(BitmapSource Image, string CameraId)> _imageWithIdSubject = new();

    // IsConnected 现在表示是否有至少一个相机连接
    private bool _isConnected;

    // isGrabbing 可能需要更精细的管理，或者表示整体状态
    private volatile bool _isGrabbing; // 使用 volatile 确保跨线程可见性

    public bool IsConnected
    {
        get
        {
            lock (_lock) // 添加 lock 以确保读取与写入同步
            {
                return _isConnected;
            }
        }
        private set
        {
            lock (_lock)
            {
                if (_isConnected == value) return;
                _isConnected = value;
                ConnectionChanged?.Invoke(null, _isConnected);
                Log.Information("Overall Hikvision Smart Camera connection status changed: {IsConnected}",
                    _isConnected);
            }
        }
    }

    public IObservable<PackageInfo> PackageStream => _packageSubject.AsObservable();
    public IObservable<BitmapSource> ImageStream => _imageSubject.AsObservable();
    public IObservable<(BitmapSource Image, string CameraId)> ImageStreamWithId => _imageWithIdSubject.AsObservable();

    public event Action<string?, bool>? ConnectionChanged;

    public bool Start()
    {
        Log.Information("Attempting to start Hikvision Smart Camera service and connect to all available devices...");
        _isGrabbing = false;
        var anyDeviceStarted = false;

        // 1. 枚举设备
        var nRet = MvCodeReader.MV_CODEREADER_EnumDevices_NET(ref _deviceList,
            MvCodeReader.MV_CODEREADER_GIGE_DEVICE | MvCodeReader.MV_CODEREADER_USB_DEVICE);
        if (nRet != MvCodeReader.MV_CODEREADER_OK)
        {
            Log.Error("Failed to enumerate Hikvision Smart Cameras. Error code: {ErrorCode:X}", nRet);
            return false;
        }

        if (_deviceList.nDeviceNum == 0)
        {
            Log.Warning("No Hikvision Smart Cameras found.");
            IsConnected = false;
            return false;
        }

        Log.Information("Found {DeviceCount} Hikvision Smart Cameras. Attempting to connect...",
            _deviceList.nDeviceNum);

        for (var i = 0; i < _deviceList.nDeviceNum; i++)
        {
            try
            {
                var pDevInfo = _deviceList.pDeviceInfo[i];
                if (pDevInfo == IntPtr.Zero)
                {
                    Log.Warning("Device info pointer at index {Index} is null. Skipping.", i);
                    continue;
                }
                var stDevInfo =
                    (MvCodeReader.MV_CODEREADER_DEVICE_INFO)Marshal.PtrToStructure(pDevInfo,
                        typeof(MvCodeReader.MV_CODEREADER_DEVICE_INFO))!;
                var cameraId = GetCameraId(stDevInfo);

                if (string.IsNullOrEmpty(cameraId))
                {
                    Log.Warning("Could not determine a unique ID for camera index {Index}. Skipping.", i);
                    continue;
                }

                Log.Information("Attempting to connect to camera: {CameraId}", cameraId);
                var device = new MvCodeReader();

                // 3. 创建句柄
                nRet = device.MV_CODEREADER_CreateHandle_NET(ref stDevInfo);
                if (nRet != MvCodeReader.MV_CODEREADER_OK)
                {
                    Log.Error("Failed to create handle for camera {CameraId}. Error code: {ErrorCode:X}", cameraId,
                        nRet);
                    continue;
                }

                // 4. 打开设备
                nRet = device.MV_CODEREADER_OpenDevice_NET();
                if (nRet != MvCodeReader.MV_CODEREADER_OK)
                {
                    Log.Error("Failed to open camera {CameraId}. Error code: {ErrorCode:X}", cameraId, nRet);
                    device.MV_CODEREADER_DestroyHandle_NET(); // Clean up handle
                    continue;
                }

                // 6. 启动抓图
                nRet = device.MV_CODEREADER_StartGrabbing_NET();
                if (nRet != MvCodeReader.MV_CODEREADER_OK)
                {
                    Log.Error("Failed to start grabbing for camera {CameraId}. Error code: {ErrorCode:X}", cameraId,
                        nRet);
                    device.MV_CODEREADER_CloseDevice_NET();
                    device.MV_CODEREADER_DestroyHandle_NET();
                    continue;
                }

                // 7. 创建并启动接收线程
                var cts = new CancellationTokenSource();
                var receiveThread = new Thread(() => ReceiveThreadProcess(device, cameraId, cts.Token))
                {
                    IsBackground = true, // Ensure thread doesn't prevent application exit
                    Name = $"HikvisionReceive_{cameraId}"
                };

                if (_activeDevices.TryAdd(cameraId, (device, receiveThread)) && _deviceCts.TryAdd(cameraId, cts))
                {
                    receiveThread.Start();
                    Log.Information("Successfully started grabbing and receive thread for camera: {CameraId}",
                        cameraId);
                    anyDeviceStarted = true;
                }
                else
                {
                    Log.Error("Failed to add camera {CameraId} to active devices dictionary. Stopping grab.", cameraId);
                    // Stop and cleanup the device itself
                    device.MV_CODEREADER_StopGrabbing_NET();
                    device.MV_CODEREADER_CloseDevice_NET();
                    device.MV_CODEREADER_DestroyHandle_NET();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Exception occurred while processing device info at index {Index}", i);
            }
        }

        _isGrabbing = anyDeviceStarted;
        IsConnected = anyDeviceStarted; // Update overall connection status

        if (anyDeviceStarted || _deviceList.nDeviceNum <= 0) return _isGrabbing;
        Log.Warning("Failed to start any of the {DeviceCount} detected cameras.", _deviceList.nDeviceNum);
        return false;
    }

    public bool Stop()
    {
        Log.Information("Attempting to stop Hikvision Smart Camera service...");
        _isGrabbing = false;
        var allStoppedCleanly = true;

        // Signal cancellation to all running threads first
        foreach (var kvp in _deviceCts)
        {
            try
            {
                kvp.Value.Cancel();
            }
            catch (ObjectDisposedException)
            {
                /* Already disposed, ignore */
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error cancelling token for camera {CameraId}", kvp.Key);
            }
        }


        // Stop and cleanup each device
        var cameraIds = _activeDevices.Keys.ToList(); // Copy keys to avoid modification issues during iteration
        foreach (var cameraId in cameraIds)
        {
            if (!_activeDevices.TryRemove(cameraId, out var deviceTuple)) continue;
            var (device, thread) = deviceTuple;
            Log.Information("Stopping camera {CameraId}...", cameraId);

            try
            {
                // 1. 停止抓图
                var nRet = device.MV_CODEREADER_StopGrabbing_NET();
                if (nRet != MvCodeReader.MV_CODEREADER_OK)
                {
                    Log.Warning("Failed to stop grabbing for camera {CameraId}. Error code: {ErrorCode:X}",
                        cameraId, nRet);
                    allStoppedCleanly = false;
                }

                // 2. 等待接收线程结束 (with timeout)
                if (thread.IsAlive)
                {
                    if (!thread.Join(TimeSpan.FromSeconds(2))) // Wait for 2 seconds
                    {
                        Log.Warning("Receive thread for camera {CameraId} did not terminate gracefully.", cameraId);
                        // Consider Thread.Abort() as a last resort, but it's generally discouraged.
                        allStoppedCleanly = false;
                    }
                }

                // 3. 关闭设备
                nRet = device.MV_CODEREADER_CloseDevice_NET();
                if (nRet != MvCodeReader.MV_CODEREADER_OK)
                {
                    Log.Warning("Failed to close camera {CameraId}. Error code: {ErrorCode:X}", cameraId, nRet);
                    allStoppedCleanly = false;
                }

                // 4. 销毁句柄
                nRet = device.MV_CODEREADER_DestroyHandle_NET();
                if (nRet != MvCodeReader.MV_CODEREADER_OK)
                {
                    Log.Warning("Failed to destroy handle for camera {CameraId}. Error code: {ErrorCode:X}",
                        cameraId, nRet);
                    allStoppedCleanly = false;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Exception occurred while stopping camera {CameraId}.", cameraId);
                allStoppedCleanly = false;
            }

            // Clean up CTS
            if (_deviceCts.TryRemove(cameraId, out var cts))
            {
                cts.Dispose();
            }
        }

        _activeDevices.Clear(); // Ensure dictionary is empty
        _deviceCts.Clear();
        IsConnected = false; // Update overall status
        Log.Information("Hikvision Smart Camera service stopped. All devices released: {AllStoppedCleanly}",
            allStoppedCleanly);
        return allStoppedCleanly;
    }

    public IEnumerable<CameraBasicInfo> GetAvailableCameras()
    {
        Log.Information("Getting available Hikvision Smart Cameras...");
        var availableCameras = new List<CameraBasicInfo>();
        // Ensure the list is updated
        var nRet = MvCodeReader.MV_CODEREADER_EnumDevices_NET(ref _deviceList,
            MvCodeReader.MV_CODEREADER_GIGE_DEVICE | MvCodeReader.MV_CODEREADER_USB_DEVICE);
        if (nRet != MvCodeReader.MV_CODEREADER_OK)
        {
            Log.Error(
                "Failed to enumerate Hikvision Smart Cameras during GetAvailableCameras. Error code: {ErrorCode:X}",
                nRet);
            return availableCameras; // Return empty list
        }

        if (_deviceList.nDeviceNum == 0)
        {
            Log.Information("No Hikvision Smart Cameras found during enumeration.");
            return availableCameras;
        }

        for (var i = 0; i < _deviceList.nDeviceNum; i++)
        {
            try
            {
                var pDevInfo = _deviceList.pDeviceInfo[i];
                if (pDevInfo == IntPtr.Zero)
                {
                    Log.Warning("Device info pointer at index {Index} is null. Skipping.", i);
                    continue;
                }

                // Check pointer before assuming PtrToStructure succeeds and use '!' to suppress warning
                var stDevInfo =
                    (MvCodeReader.MV_CODEREADER_DEVICE_INFO)Marshal.PtrToStructure(pDevInfo,
                        typeof(MvCodeReader.MV_CODEREADER_DEVICE_INFO))!;
                string? deviceName;
                string? serialNumber;

                switch (stDevInfo.nTLayerType)
                {
                    case MvCodeReader.MV_CODEREADER_GIGE_DEVICE:
                    {
                        var buffer = Marshal.UnsafeAddrOfPinnedArrayElement(stDevInfo.SpecialInfo.stGigEInfo, 0);
                        var stGigEDeviceInfo =
                            (MvCodeReader.MV_CODEREADER_GIGE_DEVICE_INFO)Marshal.PtrToStructure(buffer,
                                typeof(MvCodeReader.MV_CODEREADER_GIGE_DEVICE_INFO))!;
                        serialNumber = stGigEDeviceInfo.chSerialNumber;
                        deviceName = string.IsNullOrWhiteSpace(stGigEDeviceInfo.chUserDefinedName)
                            ? $"GEV: {stGigEDeviceInfo.chManufacturerName} {stGigEDeviceInfo.chModelName} ({serialNumber})"
                            : $"GEV: {stGigEDeviceInfo.chUserDefinedName} ({serialNumber})";
                        break;
                    }
                    case MvCodeReader.MV_CODEREADER_USB_DEVICE:
                    {
                        var buffer = Marshal.UnsafeAddrOfPinnedArrayElement(stDevInfo.SpecialInfo.stUsb3VInfo, 0);
                        var stUsbDeviceInfo =
                            (MvCodeReader.MV_CODEREADER_USB3_DEVICE_INFO)Marshal.PtrToStructure(buffer,
                                typeof(MvCodeReader.MV_CODEREADER_USB3_DEVICE_INFO))!;
                        serialNumber = stUsbDeviceInfo.chSerialNumber;
                        deviceName = string.IsNullOrWhiteSpace(stUsbDeviceInfo.chUserDefinedName)
                            ? $"USB: {stUsbDeviceInfo.chManufacturerName} {stUsbDeviceInfo.chModelName} ({serialNumber})"
                            : $"USB: {stUsbDeviceInfo.chUserDefinedName} ({serialNumber})";
                        break;
                    }
                    default:
                        Log.Warning("Unsupported device type detected: {DeviceType}", stDevInfo.nTLayerType);
                        deviceName = $"Unknown Type ({stDevInfo.nTLayerType})";
                        serialNumber = $"UnknownSN_{i}"; // Assign a temporary unique ID
                        break;
                }


                if (!string.IsNullOrEmpty(serialNumber) && !string.IsNullOrEmpty(deviceName))
                {
                    availableCameras.Add(new CameraBasicInfo { Id = serialNumber, Name = deviceName });
                    Log.Debug("Found camera: ID={CameraId}, Name={CameraName}", serialNumber, deviceName);
                }
                else
                {
                    Log.Warning("Could not retrieve valid ID or Name for device index {Index}", i);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing device info at index {Index}", i);
            }
        }

        Log.Information("Found {Count} available cameras.", availableCameras.Count);
        return availableCameras;
    }

    // Helper to get a unique ID (Serial Number preferably)
    private static string? GetCameraId(MvCodeReader.MV_CODEREADER_DEVICE_INFO stDevInfo)
    {
        try
        {
            switch (stDevInfo.nTLayerType)
            {
                case MvCodeReader.MV_CODEREADER_GIGE_DEVICE:
                {
                    var buffer = Marshal.UnsafeAddrOfPinnedArrayElement(stDevInfo.SpecialInfo.stGigEInfo, 0);
                    var stGigEDeviceInfo =
                        (MvCodeReader.MV_CODEREADER_GIGE_DEVICE_INFO)Marshal.PtrToStructure(buffer,
                            typeof(MvCodeReader.MV_CODEREADER_GIGE_DEVICE_INFO))!;
                    return stGigEDeviceInfo.chSerialNumber?.TrimEnd('\0');
                }
                case MvCodeReader.MV_CODEREADER_USB_DEVICE:
                {
                    var buffer = Marshal.UnsafeAddrOfPinnedArrayElement(stDevInfo.SpecialInfo.stUsb3VInfo, 0);
                    var stUsbDeviceInfo =
                        (MvCodeReader.MV_CODEREADER_USB3_DEVICE_INFO)Marshal.PtrToStructure(buffer,
                            typeof(MvCodeReader.MV_CODEREADER_USB3_DEVICE_INFO))!;
                    return stUsbDeviceInfo.chSerialNumber?.TrimEnd('\0');
                }
                default:
                    Log.Warning("Cannot get SerialNumber for unsupported device type: {DeviceType}",
                        stDevInfo.nTLayerType);
                    return null;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to extract camera ID from device info.");
            return null;
        }
    }


    private void ReceiveThreadProcess(MvCodeReader device, string cameraId, CancellationToken cancellationToken)
    {
        Log.Debug("Hikvision Smart Camera receive thread started for {CameraId}.", cameraId);
        var pData = IntPtr.Zero;
        var pstFrameInfoEx2 =
            Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MvCodeReader.MV_CODEREADER_IMAGE_OUT_INFO_EX2)));


        try
        {
            while (!cancellationToken.IsCancellationRequested) // Check cancellation token
            {
                // Use the passed device instance
                var nRet = device.MV_CODEREADER_GetOneFrameTimeoutEx2_NET(ref pData, pstFrameInfoEx2, 1000);

                if (nRet == MvCodeReader.MV_CODEREADER_OK)
                {
                    // Marshal the data from the pointer back into a structure
                    var stFrameInfoEx2 =
                        (MvCodeReader.MV_CODEREADER_IMAGE_OUT_INFO_EX2)Marshal.PtrToStructure(pstFrameInfoEx2,
                            typeof(MvCodeReader.MV_CODEREADER_IMAGE_OUT_INFO_EX2))!;

                    if (stFrameInfoEx2.nFrameLen > 0 && pData != IntPtr.Zero)
                    {
                        Log.Verbose("Received frame from {CameraId}. Length: {FrameLen}, PixelType: {PixelType}",
                            cameraId, stFrameInfoEx2.nFrameLen, stFrameInfoEx2.enPixelType);
                        // 1. Process Image
                        var bitmapSource = ConvertToBitmapSource(pData, stFrameInfoEx2);
                        if (bitmapSource != null)
                        {
                            // Freeze the BitmapSource to make it thread-safe before passing to other threads via Rx
                            bitmapSource.Freeze();
                            _imageSubject.OnNext(bitmapSource); // Push to general image stream
                            _imageWithIdSubject.OnNext((bitmapSource,
                                cameraId)); // Push image with its source camera ID
                        }
                        else
                        {
                            Log.Warning("Failed to convert image data to BitmapSource for frame from {CameraId}.",
                                cameraId);
                        }

                        // 2. Process Barcode Results
                        if (stFrameInfoEx2.UnparsedBcrList.pstCodeListEx2 != IntPtr.Zero)
                        {
                            var stBcrResultEx2 =
                                (MvCodeReader.MV_CODEREADER_RESULT_BCR_EX2)Marshal.PtrToStructure(
                                    stFrameInfoEx2.UnparsedBcrList.pstCodeListEx2,
                                    typeof(MvCodeReader.MV_CODEREADER_RESULT_BCR_EX2))!;

                            var
                                packageInfo = ConvertToPackageInfo(stBcrResultEx2, cameraId); // Pass cameraId if needed
                            if (packageInfo != null)
                            {
                                _packageSubject.OnNext(packageInfo); // Push package info
                            }
                        }
                        else
                        {
                            Log.Verbose("No barcode result structure found in frame from {CameraId}.", cameraId);
                        }
                    }
                    else if (pData == IntPtr.Zero)
                    {
                        Log.Warning(
                            "MV_CODEREADER_GetOneFrameTimeoutEx2_NET returned OK but pData is Zero for {CameraId}.",
                            cameraId);
                    }
                }
                else if (nRet == MvCodeReader.MV_CODEREADER_E_GC_TIMEOUT) // 使用正确的超时错误码
                {
                    Log.Verbose("Receive frame timeout for {CameraId}.", cameraId);
                }
                else if (nRet == MvCodeReader.MV_CODEREADER_E_CALLORDER && !_isGrabbing)
                {
                    // If grabbing was stopped, this error might occur. Exit gracefully.
                    Log.Information(
                        "Received expected call order error after stopping grab for {CameraId}. Exiting thread.",
                        cameraId);
                    break;
                }
                else
                {
                    // Log other errors
                    Log.Error(
                        "MV_CODEREADER_GetOneFrameTimeoutEx2_NET failed for {CameraId}. Error code: {ErrorCode:X}",
                        cameraId, nRet);
                    Thread.Sleep(100); // Prevent tight loop on error
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception in receive thread for camera {CameraId}.", cameraId);
        }
        finally
        {
            // Free the allocated memory for the structure pointer
            if (pstFrameInfoEx2 != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pstFrameInfoEx2);
            }

            Log.Debug("Hikvision Smart Camera receive thread stopping for {CameraId}.", cameraId);
        }
    }

    // 实现图像转换逻辑 (WPF BitmapSource)
    private static BitmapSource? ConvertToBitmapSource(IntPtr imageDataPtr,
        MvCodeReader.MV_CODEREADER_IMAGE_OUT_INFO_EX2 frameInfo)
    {
        if (imageDataPtr == IntPtr.Zero || frameInfo.nFrameLen == 0 || frameInfo.nWidth == 0 || frameInfo.nHeight == 0)
        {
            Log.Warning("Invalid image data or dimensions provided for BitmapSource conversion.");
            return null;
        }

        Log.Verbose(
            "Converting frame data (PixelType: {PixelType}, W: {Width}, H: {Height}, Len: {Length}) to BitmapSource...",
            frameInfo.enPixelType, frameInfo.nWidth, frameInfo.nHeight, frameInfo.nFrameLen);

        try
        {
            switch (frameInfo.enPixelType)
            {
                case MvCodeReader.MvCodeReaderGvspPixelType.PixelType_CodeReader_Gvsp_Mono8:
                {
                    int stride = frameInfo.nWidth;

                    var bitmapSource = BitmapSource.Create(
                        frameInfo.nWidth,
                        frameInfo.nHeight,
                        96,
                        96,
                        PixelFormats.Gray8,
                        null,
                        imageDataPtr,
                        (int)frameInfo.nFrameLen,
                        stride);
                    Log.Verbose("Successfully created Gray8 BitmapSource.");
                    return bitmapSource;
                }
                case MvCodeReader.MvCodeReaderGvspPixelType.PixelType_CodeReader_Gvsp_Jpeg:
                {
                    // Copy data from IntPtr to managed byte array
                    var imageBytes = new byte[frameInfo.nFrameLen];
                    Marshal.Copy(imageDataPtr, imageBytes, 0, (int)frameInfo.nFrameLen);

                    using var stream = new MemoryStream(imageBytes);
                    var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count > 0)
                    {
                        Log.Verbose("Successfully decoded JPEG BitmapSource.");
                        return decoder.Frames[0];
                    }
                    else
                    {
                        Log.Warning("JpegBitmapDecoder could not decode any frames.");
                        return null;
                    }
                }
                default:
                    Log.Warning("Unsupported pixel format for BitmapSource conversion: {PixelType}",
                        frameInfo.enPixelType);
                    return null;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex,
                "Exception during BitmapSource conversion. PixelType: {PixelType}, W: {Width}, H: {Height}, Len: {Length}",
                frameInfo.enPixelType, frameInfo.nWidth, frameInfo.nHeight, frameInfo.nFrameLen);
            return null;
        }
    }

    // 实现读码结果转换逻辑
    private static PackageInfo? ConvertToPackageInfo(MvCodeReader.MV_CODEREADER_RESULT_BCR_EX2 barcodeResult,
        string cameraId)
    {
        if (barcodeResult.nCodeNum > 0)
        {
            // 取第一个码作为示例
            var firstCodeInfo = barcodeResult.stBcrInfoEx2[0];

            // Ensure the byte array has content before converting
            var codeLength = Array.FindIndex(firstCodeInfo.chCode, b => b == 0); // Find null terminator
            if (codeLength == -1)
                codeLength = firstCodeInfo.chCode.Length; // Or use full length if no null terminator found


            if (codeLength > 0)
            {
                // Convert using appropriate encoding (UTF8 is common, but verify if needed)
                var code = Encoding.UTF8.GetString(firstCodeInfo.chCode, 0, codeLength);

                if (!string.IsNullOrEmpty(code))
                {
                    // 使用静态工厂方法创建实例
                    var packageInfo = PackageInfo.Create();
                    // 使用 Set 方法设置条码
                    packageInfo.SetBarcode(code);
                    packageInfo.SetStatus(PackageStatus.Created);


                    Log.Debug(
                        "Barcode found by {CameraId}: {Barcode} (Type: {BarcodeType}, Quality: {Quality}, Cost: {Cost}ms)",
                        cameraId,
                        packageInfo.Barcode,
                        (MvCodeReader.MV_CODEREADER_CODE_TYPE)firstCodeInfo
                            .nBarType,
                        firstCodeInfo.stCodeQuality.nOverQuality,
                        firstCodeInfo.nTotalProcCost);
                    return packageInfo;
                }
                Log.Warning("Decoded barcode string is empty for camera {CameraId} despite codeLength > 0.",
                    cameraId);
            }
            else
            {
                Log.Warning("Barcode byte array is empty or contains only null terminator for camera {CameraId}.",
                    cameraId);
            }
        }
        else
        {
            Log.Verbose("No barcode found in the result from camera {CameraId}.", cameraId);
        }

        return null;
    }


    public void Dispose()
    {
        Log.Information("Disposing Hikvision Smart Camera service...");
        Stop();

        _packageSubject.Dispose();
        _imageSubject.Dispose();
        _imageWithIdSubject.Dispose();

        foreach (var cts in _deviceCts.Values)
        {
            try
            {
                cts.Dispose();
            }
            catch
            {
                // ignored
            }
        }

        _deviceCts.Clear();
        GC.SuppressFinalize(this);
    }


    ~HikvisionSmartCameraService()
    {
        Dispose();
    }
}

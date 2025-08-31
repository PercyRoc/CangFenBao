// using System.Reactive.Linq;
// using System.Reactive.Subjects;
// using System.Windows.Media; // Required for PixelFormats
// using System.Windows.Media.Imaging;
// using Common.Models.Package;
// using DeviceService.DataSourceDevices.Camera.Models;
// using MvCodeReaderSDKNet;
// using Serilog;
// using System.Runtime.InteropServices;
// using System.Collections.Concurrent;
// using System.Text; // Required for Encoding
//
//
// namespace DeviceService.DataSourceDevices.Camera.HikvisionSmartSdk;
//
// /// <summary>
// /// 海康威视智能相机服务实现。
// /// </summary>
// public class HikvisionSmartCameraService : ICameraService
// {
//     // 使用 ConcurrentDictionary 管理多个设备及其线程
//     private readonly ConcurrentDictionary<string, (MvCodeReader device, Thread thread)> _activeDevices = new();
//
//     private MvCodeReader.MV_CODEREADER_DEVICE_INFO_LIST _deviceList; // 设备列表
//
//     private readonly ConcurrentDictionary<string, CancellationTokenSource> _deviceCts = new();
//
//     private readonly object _lock = new();
//
//     // private HikvisionSmartCameraSettings? _cameraSettings; // 可能仍需要通用设置
//     private readonly Subject<PackageInfo> _packageSubject = new();
//     private readonly Subject<BitmapSource> _imageSubject = new();
//
//     private readonly Subject<(BitmapSource Image, string CameraId)> _imageWithIdSubject = new();
//
//     // IsConnected 现在表示是否有至少一个相机连接
//     private bool _isConnected;
//
//     // isGrabbing 可能需要更精细的管理，或者表示整体状态
//     private volatile bool _isGrabbing; // 使用 volatile 确保跨线程可见性
//
//     public bool IsConnected
//     {
//         get
//         {
//             lock (_lock) // 添加 lock 以确保读取与写入同步
//             {
//                 return _isConnected;
//             }
//         }
//         private set
//         {
//             lock (_lock)
//             {
//                 if (_isConnected == value) return;
//                 _isConnected = value;
//                 ConnectionChanged?.Invoke(null, _isConnected);
//                 Log.Information("海康智能相机整体连接状态变更: {IsConnected}",
//                     _isConnected);
//             }
//         }
//     }
//
//     public IObservable<PackageInfo> PackageStream => _packageSubject.AsObservable();
//     public IObservable<BitmapSource> ImageStream => _imageSubject.AsObservable();
//     public IObservable<(BitmapSource Image, string CameraId)> ImageStreamWithId => _imageWithIdSubject.AsObservable();
//
//     public event Action<string?, bool>? ConnectionChanged;
//
//     public bool Start()
//     {
//         Log.Information("正在尝试启动海康智能相机服务并连接到所有可用设备...");
//         _isGrabbing = false;
//         var anyDeviceStarted = false;
//
//         // 1. 枚举设备
//         var nRet = MvCodeReader.MV_CODEREADER_EnumDevices_NET(ref _deviceList,
//             MvCodeReader.MV_CODEREADER_GIGE_DEVICE | MvCodeReader.MV_CODEREADER_USB_DEVICE);
//         if (nRet != MvCodeReader.MV_CODEREADER_OK)
//         {
//             Log.Error("枚举海康智能相机失败。错误码: {ErrorCode:X}", nRet);
//             return false;
//         }
//
//         if (_deviceList.nDeviceNum == 0)
//         {
//             Log.Warning("未找到海康智能相机。");
//             IsConnected = false;
//             return false;
//         }
//
//         Log.Information("发现 {DeviceCount} 台海康智能相机。正在尝试连接...",
//             _deviceList.nDeviceNum);
//
//         for (var i = 0; i < _deviceList.nDeviceNum; i++)
//         {
//             try
//             {
//                 var pDevInfo = _deviceList.pDeviceInfo[i];
//                 if (pDevInfo == IntPtr.Zero)
//                 {
//                     Log.Warning("索引 {Index} 处的设备信息指针为空，已跳过。", i);
//                     continue;
//                 }
//                 var stDevInfo =
//                     (MvCodeReader.MV_CODEREADER_DEVICE_INFO)Marshal.PtrToStructure(pDevInfo,
//                         typeof(MvCodeReader.MV_CODEREADER_DEVICE_INFO))!;
//                 var cameraId = GetCameraId(stDevInfo);
//
//                 if (string.IsNullOrEmpty(cameraId))
//                 {
//                     Log.Warning("无法确定相机索引 {Index} 的唯一ID，已跳过。", i);
//                     continue;
//                 }
//
//                 Log.Information("正在尝试连接相机: {CameraId}", cameraId);
//                 var device = new MvCodeReader();
//
//                 // 3. 创建句柄
//                 nRet = device.MV_CODEREADER_CreateHandle_NET(ref stDevInfo);
//                 if (nRet != MvCodeReader.MV_CODEREADER_OK)
//                 {
//                     Log.Error("为相机 {CameraId} 创建句柄失败。错误码: {ErrorCode:X}", cameraId,
//                         nRet);
//                     continue;
//                 }
//
//                 // 4. 打开设备
//                 nRet = device.MV_CODEREADER_OpenDevice_NET();
//                 if (nRet != MvCodeReader.MV_CODEREADER_OK)
//                 {
//                     Log.Error("打开相机 {CameraId} 失败。错误码: {ErrorCode:X}", cameraId, nRet);
//                     device.MV_CODEREADER_DestroyHandle_NET(); // Clean up handle
//                     continue;
//                 }
//
//                 // 6. 启动抓图
//                 nRet = device.MV_CODEREADER_StartGrabbing_NET();
//                 if (nRet != MvCodeReader.MV_CODEREADER_OK)
//                 {
//                     Log.Error("为相机 {CameraId} 启动抓图失败。错误码: {ErrorCode:X}", cameraId,
//                         nRet);
//                     device.MV_CODEREADER_CloseDevice_NET();
//                     device.MV_CODEREADER_DestroyHandle_NET();
//                     continue;
//                 }
//
//                 // 7. 创建并启动接收线程
//                 var cts = new CancellationTokenSource();
//                 var receiveThread = new Thread(() => ReceiveThreadProcess(device, cameraId, cts.Token))
//                 {
//                     IsBackground = true, // Ensure thread doesn't prevent application exit
//                     Name = $"HikvisionReceive_{cameraId}"
//                 };
//
//                 if (_activeDevices.TryAdd(cameraId, (device, receiveThread)) && _deviceCts.TryAdd(cameraId, cts))
//                 {
//                     receiveThread.Start();
//                     Log.Information("为相机 {CameraId} 成功启动抓图和接收线程。",
//                         cameraId);
//                     anyDeviceStarted = true;
//                 }
//                 else
//                 {
//                     Log.Error("将相机 {CameraId} 添加到活动设备字典失败。正在停止抓图。", cameraId);
//                     // Stop and cleanup the device itself
//                     device.MV_CODEREADER_StopGrabbing_NET();
//                     device.MV_CODEREADER_CloseDevice_NET();
//                     device.MV_CODEREADER_DestroyHandle_NET();
//                 }
//             }
//             catch (Exception ex)
//             {
//                 Log.Error(ex, "处理索引为 {Index} 的设备信息时发生异常。", i);
//             }
//         }
//
//         _isGrabbing = anyDeviceStarted;
//         IsConnected = anyDeviceStarted; // Update overall connection status
//
//         if (anyDeviceStarted || _deviceList.nDeviceNum <= 0) return _isGrabbing;
//         Log.Warning("启动 {DeviceCount} 台已检测到的相机均失败。", _deviceList.nDeviceNum);
//         return false;
//     }
//
//     public bool Stop()
//     {
//         Log.Information("正在尝试停止海康智能相机服务...");
//         _isGrabbing = false;
//         var allStoppedCleanly = true;
//
//         // Signal cancellation to all running threads first
//         foreach (var kvp in _deviceCts)
//         {
//             try
//             {
//                 kvp.Value.Cancel();
//             }
//             catch (ObjectDisposedException)
//             {
//                 /* Already disposed, ignore */
//             }
//             catch (Exception ex)
//             {
//                 Log.Warning(ex, "取消相机 {CameraId} 的令牌时出错。", kvp.Key);
//             }
//         }
//
//
//         // Stop and cleanup each device
//         var cameraIds = _activeDevices.Keys.ToList(); // Copy keys to avoid modification issues during iteration
//         foreach (var cameraId in cameraIds)
//         {
//             if (!_activeDevices.TryRemove(cameraId, out var deviceTuple)) continue;
//             var (device, thread) = deviceTuple;
//             Log.Information("正在停止相机 {CameraId}...", cameraId);
//
//             try
//             {
//                 // 1. 停止抓图
//                 var nRet = device.MV_CODEREADER_StopGrabbing_NET();
//                 if (nRet != MvCodeReader.MV_CODEREADER_OK)
//                 {
//                     Log.Warning("为相机 {CameraId} 停止抓图失败。错误码: {ErrorCode:X}",
//                         cameraId, nRet);
//                     allStoppedCleanly = false;
//                 }
//
//                 // 2. 等待接收线程结束 (with timeout)
//                 if (thread.IsAlive)
//                 {
//                     if (!thread.Join(TimeSpan.FromSeconds(2))) // Wait for 2 seconds
//                     {
//                         Log.Warning("相机 {CameraId} 的接收线程未正常终止。", cameraId);
//                         // Consider Thread.Abort() as a last resort, but it's generally discouraged.
//                         allStoppedCleanly = false;
//                     }
//                 }
//
//                 // 3. 关闭设备
//                 nRet = device.MV_CODEREADER_CloseDevice_NET();
//                 if (nRet != MvCodeReader.MV_CODEREADER_OK)
//                 {
//                     Log.Warning("关闭相机 {CameraId} 失败。错误码: {ErrorCode:X}", cameraId, nRet);
//                     allStoppedCleanly = false;
//                 }
//
//                 // 4. 销毁句柄
//                 nRet = device.MV_CODEREADER_DestroyHandle_NET();
//                 if (nRet != MvCodeReader.MV_CODEREADER_OK)
//                 {
//                     Log.Warning("销毁相机 {CameraId} 的句柄失败。错误码: {ErrorCode:X}",
//                         cameraId, nRet);
//                     allStoppedCleanly = false;
//                 }
//             }
//             catch (Exception ex)
//             {
//                 Log.Error(ex, "停止相机 {CameraId} 时发生异常。", cameraId);
//                 allStoppedCleanly = false;
//             }
//
//             // Clean up CTS
//             if (_deviceCts.TryRemove(cameraId, out var cts))
//             {
//                 cts.Dispose();
//             }
//         }
//
//         _activeDevices.Clear(); // Ensure dictionary is empty
//         _deviceCts.Clear();
//         IsConnected = false; // Update overall status
//         Log.Information("海康智能相机服务已停止。所有设备已释放: {AllStoppedCleanly}",
//             allStoppedCleanly);
//         return allStoppedCleanly;
//     }
//
//     public IEnumerable<CameraBasicInfo> GetAvailableCameras()
//     {
//         Log.Information("正在获取可用的海康智能相机...");
//         var availableCameras = new List<CameraBasicInfo>();
//         // Ensure the list is updated
//         var nRet = MvCodeReader.MV_CODEREADER_EnumDevices_NET(ref _deviceList,
//             MvCodeReader.MV_CODEREADER_GIGE_DEVICE | MvCodeReader.MV_CODEREADER_USB_DEVICE);
//         if (nRet != MvCodeReader.MV_CODEREADER_OK)
//         {
//             Log.Error(
//                 "在 GetAvailableCameras 期间枚举海康智能相机失败。错误码: {ErrorCode:X}",
//                 nRet);
//             return availableCameras; // Return empty list
//         }
//
//         if (_deviceList.nDeviceNum == 0)
//         {
//             Log.Information("枚举期间未找到海康智能相机。");
//             return availableCameras;
//         }
//
//         for (var i = 0; i < _deviceList.nDeviceNum; i++)
//         {
//             try
//             {
//                 var pDevInfo = _deviceList.pDeviceInfo[i];
//                 if (pDevInfo == IntPtr.Zero)
//                 {
//                     Log.Warning("索引 {Index} 处的设备信息指针为空，已跳过。", i);
//                     continue;
//                 }
//
//                 // Check pointer before assuming PtrToStructure succeeds and use '!' to suppress warning
//                 var stDevInfo =
//                     (MvCodeReader.MV_CODEREADER_DEVICE_INFO)Marshal.PtrToStructure(pDevInfo,
//                         typeof(MvCodeReader.MV_CODEREADER_DEVICE_INFO))!;
//                 string? deviceName;
//                 string? serialNumber;
//
//                 switch (stDevInfo.nTLayerType)
//                 {
//                     case MvCodeReader.MV_CODEREADER_GIGE_DEVICE:
//                     {
//                         var buffer = Marshal.UnsafeAddrOfPinnedArrayElement(stDevInfo.SpecialInfo.stGigEInfo, 0);
//                         var stGigEDeviceInfo =
//                             (MvCodeReader.MV_CODEREADER_GIGE_DEVICE_INFO)Marshal.PtrToStructure(buffer,
//                                 typeof(MvCodeReader.MV_CODEREADER_GIGE_DEVICE_INFO))!;
//                         serialNumber = stGigEDeviceInfo.chSerialNumber;
//                         deviceName = string.IsNullOrWhiteSpace(stGigEDeviceInfo.chUserDefinedName)
//                             ? $"GEV: {stGigEDeviceInfo.chManufacturerName} {stGigEDeviceInfo.chModelName} ({serialNumber})"
//                             : $"GEV: {stGigEDeviceInfo.chUserDefinedName} ({serialNumber})";
//                         break;
//                     }
//                     case MvCodeReader.MV_CODEREADER_USB_DEVICE:
//                     {
//                         var buffer = Marshal.UnsafeAddrOfPinnedArrayElement(stDevInfo.SpecialInfo.stUsb3VInfo, 0);
//                         var stUsbDeviceInfo =
//                             (MvCodeReader.MV_CODEREADER_USB3_DEVICE_INFO)Marshal.PtrToStructure(buffer,
//                                 typeof(MvCodeReader.MV_CODEREADER_USB3_DEVICE_INFO))!;
//                         serialNumber = stUsbDeviceInfo.chSerialNumber;
//                         deviceName = string.IsNullOrWhiteSpace(stUsbDeviceInfo.chUserDefinedName)
//                             ? $"USB: {stUsbDeviceInfo.chManufacturerName} {stUsbDeviceInfo.chModelName} ({serialNumber})"
//                             : $"USB: {stUsbDeviceInfo.chUserDefinedName} ({serialNumber})";
//                         break;
//                     }
//                     default:
//                         Log.Warning("检测到不支持的设备类型: {DeviceType}", stDevInfo.nTLayerType);
//                         deviceName = $"Unknown Type ({stDevInfo.nTLayerType})";
//                         serialNumber = $"UnknownSN_{i}"; // Assign a temporary unique ID
//                         break;
//                 }
//
//
//                 if (!string.IsNullOrEmpty(serialNumber) && !string.IsNullOrEmpty(deviceName))
//                 {
//                     availableCameras.Add(new CameraBasicInfo { Id = serialNumber, Name = deviceName });
//                     Log.Debug("发现相机: ID={CameraId}, 名称={CameraName}", serialNumber, deviceName);
//                 }
//                 else
//                 {
//                     Log.Warning("无法检索索引为 {Index} 的设备的有效ID或名称。", i);
//                 }
//             }
//             catch (Exception ex)
//             {
//                 Log.Error(ex, "处理索引为 {Index} 的设备信息时出错。", i);
//             }
//         }
//
//         Log.Information("发现 {Count} 台可用相机。", availableCameras.Count);
//         return availableCameras;
//     }
//
//     // Helper to get a unique ID (Serial Number preferably)
//     private static string? GetCameraId(MvCodeReader.MV_CODEREADER_DEVICE_INFO stDevInfo)
//     {
//         try
//         {
//             switch (stDevInfo.nTLayerType)
//             {
//                 case MvCodeReader.MV_CODEREADER_GIGE_DEVICE:
//                 {
//                     var buffer = Marshal.UnsafeAddrOfPinnedArrayElement(stDevInfo.SpecialInfo.stGigEInfo, 0);
//                     var stGigEDeviceInfo =
//                         (MvCodeReader.MV_CODEREADER_GIGE_DEVICE_INFO)Marshal.PtrToStructure(buffer,
//                             typeof(MvCodeReader.MV_CODEREADER_GIGE_DEVICE_INFO))!;
//                     return stGigEDeviceInfo.chSerialNumber?.TrimEnd('\0');
//                 }
//                 case MvCodeReader.MV_CODEREADER_USB_DEVICE:
//                 {
//                     var buffer = Marshal.UnsafeAddrOfPinnedArrayElement(stDevInfo.SpecialInfo.stUsb3VInfo, 0);
//                     var stUsbDeviceInfo =
//                         (MvCodeReader.MV_CODEREADER_USB3_DEVICE_INFO)Marshal.PtrToStructure(buffer,
//                             typeof(MvCodeReader.MV_CODEREADER_USB3_DEVICE_INFO))!;
//                     return stUsbDeviceInfo.chSerialNumber?.TrimEnd('\0');
//                 }
//                 default:
//                     Log.Warning("无法获取不支持设备类型的序列号: {DeviceType}",
//                         stDevInfo.nTLayerType);
//                     return null;
//             }
//         }
//         catch (Exception ex)
//         {
//             Log.Error(ex, "从设备信息中提取相机ID失败。");
//             return null;
//         }
//     }
//
//
//     private void ReceiveThreadProcess(MvCodeReader device, string cameraId, CancellationToken cancellationToken)
//     {
//         Log.Debug("海康智能相机接收线程已为 {CameraId} 启动。", cameraId);
//         var pData = IntPtr.Zero;
//         var pstFrameInfoEx2 =
//             Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MvCodeReader.MV_CODEREADER_IMAGE_OUT_INFO_EX2)));
//
//
//         try
//         {
//             while (!cancellationToken.IsCancellationRequested) // Check cancellation token
//             {
//                 // Use the passed device instance
//                 var nRet = device.MV_CODEREADER_GetOneFrameTimeoutEx2_NET(ref pData, pstFrameInfoEx2, 1000);
//
//                 if (nRet == MvCodeReader.MV_CODEREADER_OK)
//                 {
//                     // Marshal the data from the pointer back into a structure
//                     var stFrameInfoEx2 =
//                         (MvCodeReader.MV_CODEREADER_IMAGE_OUT_INFO_EX2)Marshal.PtrToStructure(pstFrameInfoEx2,
//                             typeof(MvCodeReader.MV_CODEREADER_IMAGE_OUT_INFO_EX2))!;
//
//                     if (stFrameInfoEx2.nFrameLen > 0 && pData != IntPtr.Zero)
//                     {
//                         Log.Verbose("从 {CameraId} 收到帧。长度: {FrameLen}, 像素类型: {PixelType}",
//                             cameraId, stFrameInfoEx2.nFrameLen, stFrameInfoEx2.enPixelType);
//                         // 1. Process Image
//                         var bitmapSource = ConvertToBitmapSource(pData, stFrameInfoEx2);
//                         if (bitmapSource != null)
//                         {
//                             // Freeze the BitmapSource to make it thread-safe before passing to other threads via Rx
//                             bitmapSource.Freeze();
//                             _imageSubject.OnNext(bitmapSource); // Push to general image stream
//                             _imageWithIdSubject.OnNext((bitmapSource,
//                                 cameraId));
//                         }
//                         else
//                         {
//                             Log.Warning("将来自 {CameraId} 的帧的图像数据转换为 BitmapSource 失败。",
//                                 cameraId);
//                         }
//
//                         // 2. Process Barcode Results
//                         if (stFrameInfoEx2.UnparsedBcrList.pstCodeListEx2 != IntPtr.Zero)
//                         {
//                             var stBcrResultEx2 =
//                                 (MvCodeReader.MV_CODEREADER_RESULT_BCR_EX2)Marshal.PtrToStructure(
//                                     stFrameInfoEx2.UnparsedBcrList.pstCodeListEx2,
//                                     typeof(MvCodeReader.MV_CODEREADER_RESULT_BCR_EX2))!;
//
//                             var packageInfo = ConvertToPackageInfo(stBcrResultEx2, cameraId);
//                             if (packageInfo != null)
//                             {
//                                 _packageSubject.OnNext(packageInfo);
//                             }
//                         }
//                         else
//                         {
//                             Log.Verbose("在来自 {CameraId} 的帧中未找到条码结果结构。", cameraId);
//                         }
//                     }
//                     else if (pData == IntPtr.Zero)
//                     {
//                         Log.Warning(
//                             "MV_CODEREADER_GetOneFrameTimeoutEx2_NET 返回 OK 但 pData 对于 {CameraId} 为零。",
//                             cameraId);
//                     }
//                 }
//                 else if (nRet == MvCodeReader.MV_CODEREADER_E_GC_TIMEOUT) // 使用正确的超时错误码
//                 {
//                     Log.Verbose("相机 {CameraId} 接收帧超时。", cameraId);
//                 }
//                 else if (nRet == MvCodeReader.MV_CODEREADER_E_NODATA) // 0x80020006 - 正常超时，相机等待触发信号
//                 {
//                     // 这只是一个正常的超时，意味着相机还在等待触发信号。
//                     // 我们什么都不做，直接进入下一次循环继续等待。
//                 }
//                 else if (nRet == MvCodeReader.MV_CODEREADER_E_CALLORDER && !_isGrabbing)
//                 {
//                     // If grabbing was stopped, this error might occur. Exit gracefully.
//                     Log.Information(
//                         "为 {CameraId} 停止抓图后收到预期的调用顺序错误。正在退出线程。",
//                         cameraId);
//                     break;
//                 }
//                 else
//                 {
//                     // 现在，能进入这个分支的都是我们没预料到的真正错误。
//                     Log.Error(
//                         "获取相机图像时发生意外错误 {CameraId}. 错误码: {ErrorCode:X}",
//                         cameraId, nRet);
//                     Thread.Sleep(100); // Prevent tight loop on error
//                 }
//             }
//         }
//         catch (Exception ex)
//         {
//             Log.Error(ex, "相机 {CameraId} 的接收线程发生异常。", cameraId);
//         }
//         finally
//         {
//             // Free the allocated memory for the structure pointer
//             if (pstFrameInfoEx2 != IntPtr.Zero)
//             {
//                 Marshal.FreeHGlobal(pstFrameInfoEx2);
//             }
//
//             Log.Debug("海康智能相机接收线程正在为 {CameraId} 停止。", cameraId);
//         }
//     }
//
//     // 实现图像转换逻辑 (WPF BitmapSource)
//     private static BitmapSource? ConvertToBitmapSource(IntPtr imageDataPtr,
//         MvCodeReader.MV_CODEREADER_IMAGE_OUT_INFO_EX2 frameInfo)
//     {
//         if (imageDataPtr == IntPtr.Zero || frameInfo.nFrameLen == 0 || frameInfo.nWidth == 0 || frameInfo.nHeight == 0)
//         {
//             Log.Warning("为 BitmapSource 转换提供了无效的图像数据或尺寸。");
//             return null;
//         }
//
//         Log.Verbose(
//             "正在将帧数据 (像素类型: {PixelType}, 宽: {Width}, 高: {Height}, 长度: {Length}) 转换为 BitmapSource...",
//             frameInfo.enPixelType, frameInfo.nWidth, frameInfo.nHeight, frameInfo.nFrameLen);
//
//         try
//         {
//             switch (frameInfo.enPixelType)
//             {
//                 case MvCodeReader.MvCodeReaderGvspPixelType.PixelType_CodeReader_Gvsp_Mono8:
//                 {
//                     int stride = frameInfo.nWidth;
//
//                     var bitmapSource = BitmapSource.Create(
//                         frameInfo.nWidth,
//                         frameInfo.nHeight,
//                         96,
//                         96,
//                         PixelFormats.Gray8,
//                         null,
//                         imageDataPtr,
//                         (int)frameInfo.nFrameLen,
//                         stride);
//                     Log.Verbose("成功创建 Gray8 BitmapSource。");
//                     return bitmapSource;
//                 }
//                 case MvCodeReader.MvCodeReaderGvspPixelType.PixelType_CodeReader_Gvsp_Jpeg:
//                 {
//                     // Copy data from IntPtr to managed byte array
//                     var imageBytes = new byte[frameInfo.nFrameLen];
//                     Marshal.Copy(imageDataPtr, imageBytes, 0, (int)frameInfo.nFrameLen);
//
//                     using var stream = new MemoryStream(imageBytes);
//                     var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat,
//                         BitmapCacheOption.OnLoad);
//                     if (decoder.Frames.Count > 0)
//                     {
//                         Log.Verbose("成功解码 JPEG BitmapSource。");
//                         return decoder.Frames[0];
//                     }
//                     else
//                     {
//                         Log.Warning("JpegBitmapDecoder 无法解码任何帧。");
//                         return null;
//                     }
//                 }
//                 default:
//                     Log.Warning("不支持的像素格式，无法转换为 BitmapSource: {PixelType}",
//                         frameInfo.enPixelType);
//                     return null;
//             }
//         }
//         catch (Exception ex)
//         {
//             Log.Error(ex,
//                 "BitmapSource 转换期间发生异常。像素类型: {PixelType}, 宽: {Width}, 高: {Height}, 长度: {Length}",
//                 frameInfo.enPixelType, frameInfo.nWidth, frameInfo.nHeight, frameInfo.nFrameLen);
//             return null;
//         }
//     }
//
//     // 实现读码结果转换逻辑
//     private static PackageInfo? ConvertToPackageInfo(MvCodeReader.MV_CODEREADER_RESULT_BCR_EX2 barcodeResult,
//         string cameraId)
//     {
//         if (barcodeResult.nCodeNum > 0)
//         {
//             // 取第一个码作为示例
//             var firstCodeInfo = barcodeResult.stBcrInfoEx2[0];
//
//             // Ensure the byte array has content before converting
//             var codeLength = Array.FindIndex(firstCodeInfo.chCode, b => b == 0); // Find null terminator
//             if (codeLength == -1)
//                 codeLength = firstCodeInfo.chCode.Length; // Or use full length if no null terminator found
//
//
//             if (codeLength > 0)
//             {
//                 // Convert using appropriate encoding (UTF8 is common, but verify if needed)
//                 var code = Encoding.UTF8.GetString(firstCodeInfo.chCode, 0, codeLength);
//
//                 if (!string.IsNullOrEmpty(code))
//                 {
//                     // 使用静态工厂方法创建实例
//                     var packageInfo = PackageInfo.Create();
//                     // 使用 Set 方法设置条码
//                     packageInfo.SetBarcode(code);
//                     packageInfo.SetStatus(PackageStatus.Created);
//
//
//                     Log.Debug(
//                         "相机 {CameraId} 发现条码: {Barcode} (类型: {BarcodeType}, 质量: {Quality}, 耗时: {Cost}ms)",
//                         cameraId,
//                         packageInfo.Barcode,
//                         (MvCodeReader.MV_CODEREADER_CODE_TYPE)firstCodeInfo
//                             .nBarType,
//                         firstCodeInfo.stCodeQuality.nOverQuality,
//                         firstCodeInfo.nTotalProcCost);
//                     return packageInfo;
//                 }
//                 Log.Warning("相机 {CameraId} 解码后的条码字符串为空，尽管 codeLength > 0。",
//                     cameraId);
//             }
//             else
//             {
//                 Log.Warning("相机 {CameraId} 的条码字节数组为空或仅包含空终止符。",
//                     cameraId);
//             }
//         }
//         else
//         {
//             Log.Verbose("相机 {CameraId} 的结果中未找到条码。", cameraId);
//         }
//
//         return null;
//     }
//
//
//     public void Dispose()
//     {
//         Log.Information("正在处置海康智能相机服务...");
//         Stop();
//
//         _packageSubject.Dispose();
//         _imageSubject.Dispose();
//         _imageWithIdSubject.Dispose();
//
//         foreach (var cts in _deviceCts.Values)
//         {
//             try
//             {
//                 cts.Dispose();
//             }
//             catch
//             {
//                 // ignored
//             }
//         }
//
//         _deviceCts.Clear();
//         GC.SuppressFinalize(this);
//     }
//
//
//     ~HikvisionSmartCameraService()
//     {
//         Dispose();
//     }
// }


using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;
using Camera.Interface;
using Camera.Models.Settings;
using Common.Models.Package;
using Common.Services.Settings;
using Serilog;
using System.IO;
using System.Windows.Media;
using MVIDCodeReaderNet;
using System.Windows;
using Pen = System.Windows.Media.Pen;

namespace Camera.Services.Implementations.Hikvision.Industrial
{
    /// <summary>
    /// 海康威视工业读码器相机服务 (MVIDCodeReaderNet SDK)
    /// 适配到 Camera 模块, 通过 ISettingsService 加载配置。
    /// </summary>
    public sealed class HikvisionIndustrialCameraService : ICameraService
    {
        private readonly ISettingsService _settingsService;
        private readonly Subject<PackageInfo> _packageSubject = new();
        private readonly Subject<(BitmapSource Image, string CameraId)> _imageWithIdSubject = new();
        private bool _disposedValue;
        private string? _lastPublishedCombinedBarcode;

        private MVIDCodeReader? _device;
        private MVIDCodeReader.MVID_CAMERA_INFO_LIST _enumeratedDeviceList;
        private string? _currentDeviceSerialNumber;
        private readonly MVIDCodeReader.cbOutputdelegate? _imageCallbackDelegate;
        private readonly MVIDCodeReader.cbExceptiondelegate? _exceptionCallbackDelegate;

        public bool IsConnected { get; private set; }
        public IObservable<PackageInfo> PackageStream => _packageSubject.AsObservable();
        public IObservable<(BitmapSource Image, string CameraId)> ImageStreamWithId => _imageWithIdSubject.AsObservable();
        public event Action<string?, bool>? ConnectionChanged;

        public HikvisionIndustrialCameraService(ISettingsService settingsService)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _imageCallbackDelegate = HandleImageCallback;
            _exceptionCallbackDelegate = HandleExceptionCallback;
            Log.Information("[海康工业相机服务] 实例已创建. HashCode: {HashCode}", GetHashCode());
        }

        public bool Start()
        {
            if (IsConnected)
            {
                Log.Information("[海康工业相机服务] 服务已连接: {SerialNumber}", _currentDeviceSerialNumber);
                return true;
            }

            var overallSettings = _settingsService.LoadSettings<CameraOverallSettings>();

            var barcodeTypeSettings = overallSettings.BarcodeType;

            Log.Information("[海康工业相机服务] 正在启动...");

            try
            {
                if (!EnumerateDevicesInternal()) return false;

                if (_enumeratedDeviceList.nDeviceNum == 0)
                {
                    Log.Warning("[海康工业相机服务] 未找到任何海康读码器设备。");
                    return false;
                }

                // Connect to the first available device
                const int deviceIndexToConnect = 0;
                Log.Information("[海康工业相机服务] 将尝试连接第一个枚举到的设备 (索引 0)。");

                uint handleType = MVIDCodeReader.MVID_BCR | MVIDCodeReader.MVID_TDCR; // Default to both
                if (barcodeTypeSettings.BarcodeTypes.Any())
                {
                    bool enable1D = barcodeTypeSettings.BarcodeTypes.Any(bt => Is1DBarcodeType(bt.TypeId) && bt.IsEnabled);
                    bool enable2D = barcodeTypeSettings.BarcodeTypes.Any(bt => Is2DBarcodeType(bt.TypeId) && bt.IsEnabled);

                    switch (enable1D)
                    {
                        case true when enable2D:
                            handleType = MVIDCodeReader.MVID_BCR | MVIDCodeReader.MVID_TDCR;
                            Log.Information("[海康工业相机服务] 根据设置，启用 1D 和 2D 条码读取。");
                            break;
                        case true:
                            handleType = MVIDCodeReader.MVID_BCR;
                            Log.Information("[海康工业相机服务] 根据设置，仅启用 1D 条码读取。");
                            break;
                        default:
                        {
                            if (enable2D)
                            {
                                handleType = MVIDCodeReader.MVID_TDCR;
                                Log.Information("[海康工业相机服务] 根据设置，仅启用 2D 条码读取。");
                            }
                            else
                            {
                                Log.Warning("[海康工业相机服务] 未在 BarcodeTypeSettings 中启用任何已知类型的条码，将默认不尝试读取任何条码类型 (handleType=0)。或考虑默认两者？");
                                handleType = MVIDCodeReader.MVID_BCR | MVIDCodeReader.MVID_TDCR; 
                                Log.Information("[海康工业相机服务] 由于未指定特定条码类型，默认启用 1D 和 2D 条码读取。");
                            }

                            break;
                        }
                    }
                }
                else
                {
                    Log.Information("[海康工业相机服务] BarcodeTypeSettings 为空或无条码类型定义，默认启用 1D 和 2D 条码读取。");
                }

                if (!ConnectToDevice(deviceIndexToConnect, handleType, true))
                {
                    return false;
                }

                Log.Information("[海康工业相机服务] 服务启动成功. 设备: {SerialNumber}", _currentDeviceSerialNumber);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[海康工业相机服务] 启动服务时发生异常。");
                StopInternal(); // Ensure cleanup on failure
                return false;
            }
        }

        public bool Stop()
        {
            return StopInternal();
        }

        private bool StopInternal()
        {
            string? disconnectedSn = _currentDeviceSerialNumber;
            Log.Information("[海康工业相机服务] 正在停止服务... 当前连接: {SerialNumber}", disconnectedSn ?? "N/A");

            if (!IsConnected && _device == null)
            {
                Log.Information("[海康工业相机服务] 服务已停止或未初始化。");
                return true;
            }

            try
            {
                if (_device != null)
                {
                    Log.Information("[海康工业相机服务] 正在停止图像采集... SN: {SerialNumber}", disconnectedSn);
                    var nRetStop = _device.MVID_CR_CAM_StopGrabbing_NET();
                    if (nRetStop != MVIDCodeReader.MVID_CR_OK)
                    {
                        Log.Error("[海康工业相机服务] 停止采集图像失败. SN: {SerialNumber}, ErrorCode: {ErrorCode:X8}", disconnectedSn, nRetStop);
                    }
                    else
                    {
                        Log.Information("[海康工业相机服务] 图像采集已停止. SN: {SerialNumber}", disconnectedSn);
                    }


                    Log.Information("[海康工业相机服务] 正在销毁 SDK 句柄... SN: {SerialNumber}", disconnectedSn);
                    var nRetDestroy = _device.MVID_CR_DestroyHandle_NET();
                    if (nRetDestroy != MVIDCodeReader.MVID_CR_OK)
                    {
                        Log.Error("[海康工业相机服务] 销毁 SDK 句柄失败. SN: {SerialNumber}, ErrorCode: {ErrorCode:X8}", disconnectedSn, nRetDestroy);
                    }
                    else
                    {
                        Log.Information("[海康工业相机服务] SDK 句柄已销毁. SN: {SerialNumber}", disconnectedSn);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[海康工业相机服务] 停止服务时发生异常. SN: {SerialNumber}", disconnectedSn);
            }
            finally
            {
                _device = null;
                if (IsConnected) // Only change and notify if it was connected
                {
                    IsConnected = false;
                    ConnectionChanged?.Invoke(disconnectedSn, false); // Use SN from before it's nulled
                    Log.Information("[海康工业相机服务] 设备连接已断开: {SerialNumber}", disconnectedSn ?? "N/A");
                }
                _currentDeviceSerialNumber = null;
            }
            Log.Information("[海康工业相机服务] 服务已停止。");
            return true;
        }

        private bool EnumerateDevicesInternal()
        {
            _enumeratedDeviceList = new MVIDCodeReader.MVID_CAMERA_INFO_LIST(); // Reset
            var nRet = MVIDCodeReader.MVID_CR_CAM_EnumDevices_NET(ref _enumeratedDeviceList);
            if (nRet != MVIDCodeReader.MVID_CR_OK)
            {
                Log.Error("[海康工业相机服务] 枚举海康读码器设备失败 (MVID_CR_CAM_EnumDevices_NET): {ErrorCode:X8}", nRet);
                return false;
            }
            Log.Information("[海康工业相机服务] 枚举到 {DeviceCount} 个海康读码器设备。", _enumeratedDeviceList.nDeviceNum);

            // 新增：详细日志输出每台相机信息
            for (uint i = 0; i < _enumeratedDeviceList.nDeviceNum; i++)
            {
                if (_enumeratedDeviceList.pstCamInfo == null || _enumeratedDeviceList.pstCamInfo[i] == IntPtr.Zero)
                {
                    Log.Warning("[海康工业相机服务] (Enumerate) 设备信息指针为空，索引: {Index}", i);
                    continue;
                }
                try
                {
                    var deviceInfo = (MVIDCodeReader.MVID_CAMERA_INFO)Marshal.PtrToStructure(
                        _enumeratedDeviceList.pstCamInfo[i], typeof(MVIDCodeReader.MVID_CAMERA_INFO))!;
                    string serialNumber = deviceInfo.chSerialNumber ?? $"UnknownSN_{i}";
                    string parsedUserDefinedName = Encoding.UTF8.GetString(deviceInfo.chUserDefinedName).TrimEnd('\0');
                    string baseModelName = (deviceInfo.chModelName ?? string.Empty).TrimEnd('\0');
                    if (string.IsNullOrWhiteSpace(baseModelName)) baseModelName = "海康读码器";
                    string finalModelName;
                    switch (deviceInfo.nCamType)
                    {
                        case MVIDCodeReader.MVID_GIGE_CAM:
                            finalModelName = $"GigE ({baseModelName})";
                            break;
                        case MVIDCodeReader.MVID_USB_CAM:
                            finalModelName = $"USB ({baseModelName})";
                            break;
                        default:
                            finalModelName = baseModelName;
                            break;
                    }
                    string mac = $"{(deviceInfo.nMacAddrHigh >> 8) & 0xFF:X2}:{deviceInfo.nMacAddrHigh & 0xFF:X2}:{(deviceInfo.nMacAddrLow >> 24) & 0xFF:X2}:{(deviceInfo.nMacAddrLow >> 16) & 0xFF:X2}:{(deviceInfo.nMacAddrLow >> 8) & 0xFF:X2}:{deviceInfo.nMacAddrLow & 0xFF:X2}";
                    string ip = string.Join(".", BitConverter.GetBytes(deviceInfo.nCurrentIp));
                    Log.Information(
                        "[海康工业相机] 详细信息: 序号={Index}, 类型={Type}, 厂商={Manufacturer}, 型号={Model}, 版本={Version}, 序列号={Serial}, 用户名={UserName}, MAC={Mac}, IP={Ip}, USB_VID={Vid}, USB_PID={Pid}, 设备编号={DevNum}, 选中={Selected}, 厂商自定义={ManuInfo}",
                        i,
                        deviceInfo.nCamType,
                        deviceInfo.chManufacturerName,
                        deviceInfo.chModelName,
                        deviceInfo.chDeviceVersion,
                        deviceInfo.chSerialNumber,
                        Encoding.UTF8.GetString(deviceInfo.chUserDefinedName).TrimEnd('\0'),
                        mac,
                        ip,
                        deviceInfo.idVendor,
                        deviceInfo.idProduct,
                        deviceInfo.nDeviceNumber,
                        deviceInfo.bSelectDevice,
                        deviceInfo.chManufacturerSpecificInfo
                    );
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[海康工业相机服务] (Enumerate) 处理设备信息时出错，索引: {Index}", i);
                }
            }
            return true;
        }


        private bool ConnectToDevice(int deviceIndex, uint readerHandleType, bool setTriggerModeOff)
        {
            if (deviceIndex < 0 || deviceIndex >= _enumeratedDeviceList.nDeviceNum ||
                _enumeratedDeviceList.pstCamInfo == null || _enumeratedDeviceList.pstCamInfo[deviceIndex] == IntPtr.Zero)
            {
                Log.Error("[海康工业相机服务] 设备索引 {Index} 无效或设备信息不可用。", deviceIndex);
                return false;
            }

            _device = new MVIDCodeReader(); // Create instance

            var nRet = _device.MVID_CR_CreateHandle_NET(readerHandleType);
            if (nRet != MVIDCodeReader.MVID_CR_OK)
            {
                Log.Error("[海康工业相机服务] 创建 SDK 句柄失败 (MVID_CR_CreateHandle_NET): ErrorCode={ErrorCode:X8}", nRet);
                _device = null;
                return false;
            }
            Log.Debug("[海康工业相机服务] SDK 句柄创建成功。");

            nRet = _device.MVID_CR_CAM_BindDevice_NET(_enumeratedDeviceList.pstCamInfo[deviceIndex]);
            if (nRet != MVIDCodeReader.MVID_CR_OK)
            {
                Log.Error("[海康工业相机服务] 绑定设备失败 (MVID_CR_CAM_BindDevice_NET): ErrorCode={ErrorCode:X8}", nRet);
                _device.MVID_CR_DestroyHandle_NET(); // Clean up handle
                _device = null;
                return false;
            }

            // Store serial number of connected device
            var deviceInfo = (MVIDCodeReader.MVID_CAMERA_INFO)Marshal.PtrToStructure(
                _enumeratedDeviceList.pstCamInfo[deviceIndex], typeof(MVIDCodeReader.MVID_CAMERA_INFO))!;
            _currentDeviceSerialNumber = deviceInfo.chSerialNumber;
            Log.Information("[海康工业相机服务] 设备绑定成功: {SerialNumber}", _currentDeviceSerialNumber);


            if (setTriggerModeOff)
            {
                // 设置触发模式为off (连续采集)
                // 0 for "Off" (continuous mode), 1 for "On" (trigger mode)
                nRet = _device.MVID_CR_CAM_SetEnumValue_NET("TriggerMode", 0); 
                if (nRet != MVIDCodeReader.MVID_CR_OK)
                {
                    Log.Warning("[海康工业相机服务] 设置触发模式为关闭失败 (TriggerMode=0): ErrorCode={ErrorCode:X8}. 相机可能使用默认触发模式。", nRet);
                }
                else
                {
                    Log.Debug("[海康工业相机服务] 触发模式已成功设置为关闭 (连续采集)。");
                }
            }
            else
            {
                 Log.Information("[海康工业相机服务] 根据配置，未设置触发模式 (将使用相机当前或默认的触发模式)。");
            }


            // 注册异常回调
            if (_exceptionCallbackDelegate != null)
            {
                nRet = _device.MVID_CR_RegisterExceptionCallBack_NET(_exceptionCallbackDelegate, IntPtr.Zero);
                if (nRet != MVIDCodeReader.MVID_CR_OK) Log.Error("[海康工业相机服务] 注册异常回调失败: ErrorCode={ErrorCode:X8}", nRet);
                else Log.Debug("[海康工业相机服务] 异常回调注册成功。");
            }

            // 注册图像数据回调
            if (_imageCallbackDelegate != null)
            {
                nRet = _device.MVID_CR_CAM_RegisterImageCallBack_NET(_imageCallbackDelegate, IntPtr.Zero);
                if (nRet != MVIDCodeReader.MVID_CR_OK)
                {
                    Log.Error("[海康工业相机服务] 注册图像回调失败: ErrorCode={ErrorCode:X8}", nRet);
                    _device.MVID_CR_DestroyHandle_NET();
                    _device = null;
                    _currentDeviceSerialNumber = null;
                    return false; // Critical failure
                }
                Log.Debug("[海康工业相机服务] 图像回调注册成功。");
            }
            else
            {
                 Log.Error("[海康工业相机服务] 图像回调委托 (_imageCallbackDelegate) 为空，无法注册。");
                 _device.MVID_CR_DestroyHandle_NET();
                 _device = null;
                 _currentDeviceSerialNumber = null;
                 return false; // Critical failure
            }

            // 开始取流
            nRet = _device.MVID_CR_CAM_StartGrabbing_NET();
            if (nRet != MVIDCodeReader.MVID_CR_OK)
            {
                Log.Error("[海康工业相机服务] 开始采集图像失败 (MVID_CR_CAM_StartGrabbing_NET): ErrorCode={ErrorCode:X8}", nRet);
                _device.MVID_CR_DestroyHandle_NET(); // Attempt cleanup
                _device = null;
                _currentDeviceSerialNumber = null;
                return false;
            }
            Log.Information("[海康工业相机服务] 开始图像采集: {SerialNumber}", _currentDeviceSerialNumber);

            IsConnected = true;
            ConnectionChanged?.Invoke(_currentDeviceSerialNumber, true);
            return true;
        }

        private void HandleExceptionCallback(uint nMsgType, IntPtr pUser)
        {
            string errorMessage = $"[海康工业相机服务] SDK 异常回调: Type={nMsgType:X8}, SN: {_currentDeviceSerialNumber ?? "N/A"}";
            if (nMsgType == MVIDCodeReader.MVID_EXCEPTION_DEV_DISCONNECT)
            {
                errorMessage = $"[海康工业相机服务] 设备已断开连接 (MVID_EXCEPTION_DEV_DISCONNECT). SN: {_currentDeviceSerialNumber ?? "N/A"}";
                Log.Error(errorMessage);
                // Schedule StopInternal to run on a thread pool thread to avoid blocking SDK callback thread
                // and to correctly manage state transitions and event invocations.
                Task.Run(() =>
                {
                    if (IsConnected) // Check if we believed it was connected
                    {
                        Log.Warning("[海康工业相机服务] 设备断开事件触发 (SN: {SN}), 将执行停止和清理...", _currentDeviceSerialNumber ?? "N/A");
                        StopInternal(); // This will set IsConnected to false and notify
                    }
                });
            }
            else if (nMsgType == MVIDCodeReader.MVID_EXCEPTION_SOFTDOG_DISCONNECT) // Assuming this enum exists or similar
            {
                errorMessage = $"[海康工业相机服务] 加密狗已断开连接 (MVID_EXCEPTION_SOFTDOG_DISCONNECT). SN: {_currentDeviceSerialNumber ?? "N/A"}";
                Log.Error(errorMessage);
                // Potentially also trigger StopInternal or other specific logic
            }
            else
            {
                Log.Warning(errorMessage);
            }
        }

        private void HandleImageCallback(IntPtr pstOutput, IntPtr pUser)
        {
            if (pstOutput == IntPtr.Zero)
            {
                Log.Warning("[海康工业相机服务] 图像回调接收到空指针 (pstOutput is null). SN: {SN}", _currentDeviceSerialNumber ?? "N/A");
                return;
            }

            try
            {
                var stCamOutputInfo = (MVIDCodeReader.MVID_CAM_OUTPUT_INFO)Marshal.PtrToStructure(pstOutput, typeof(MVIDCodeReader.MVID_CAM_OUTPUT_INFO))!;
                var currentCameraIdForEvent = _currentDeviceSerialNumber ?? "UnknownIndustrialCam";

                BitmapSource? originalBitmapSource = null;
                if (stCamOutputInfo.stImage.pImageBuf != IntPtr.Zero && stCamOutputInfo.stImage.nImageLen > 0)
                {
                    originalBitmapSource = ConvertImageDataToBitmapSource(stCamOutputInfo.stImage);
                }

                var finalBitmapSource = originalBitmapSource; // 默认使用原始图像
                var allCodeInfosForThisCallback = new List<MVIDCodeReader.MVID_CODE_INFO>();

                if (stCamOutputInfo.stCodeList is { nCodeNum: > 0, stCodeInfo: not null })
                {
                    for (var i = 0; i < stCamOutputInfo.stCodeList.nCodeNum; i++)
                    {
                        if (i >= stCamOutputInfo.stCodeList.stCodeInfo.Length)
                        {
                            Log.Warning("[海康工业相机服务] CodeInfo 数组索引越界 at {Index}. SN: {SN}", i, currentCameraIdForEvent);
                            continue;
                        }
                        var codeInfo = stCamOutputInfo.stCodeList.stCodeInfo[i];
                        var barcode = codeInfo.strCode?.TrimEnd('\0') ?? string.Empty;
                        if (!string.IsNullOrEmpty(barcode))
                        {
                            allCodeInfosForThisCallback.Add(codeInfo);
                        }
                    }
                }

                if (originalBitmapSource != null && allCodeInfosForThisCallback.Count != 0)
                {
                    var bitmapWithBorders = DrawBarcodeBordersOnImage(originalBitmapSource, allCodeInfosForThisCallback);
                    if (bitmapWithBorders != null)
                    {
                        finalBitmapSource = bitmapWithBorders;
                    }
                    else
                    {
                        Log.Warning("[海康工业相机服务] 无法在图像上绘制条码边框，将使用原始图像。SN: {SN}", currentCameraIdForEvent);
                    }
                }

                if (finalBitmapSource != null)
                {
                    // 推送（可能已处理的）图像给 ImageStreamWithId
                    _imageWithIdSubject.OnNext((finalBitmapSource, currentCameraIdForEvent));
                }
                else if (originalBitmapSource == null && stCamOutputInfo.stImage.pImageBuf != IntPtr.Zero && stCamOutputInfo.stImage.nImageLen > 0)
                {
                    // 即使没有条码或绘制失败，如果原始图像转换成功了，也应该尝试推送原始图像
                    // 但上面的逻辑是 finalBitmapSource 初始化为 originalBitmapSource，所以这里可能不需要
                    // 除非 ConvertImageDataToBitmapSource 返回 null 但pImageBuf有效，这种情况会在ConvertImageDataToBitmapSource内部记录
                }
                else
                {
                    // Log.Verbose("[海康工业相机服务] 回调中无有效图像数据可供推送. Frame: {FrameNum}, SN: {SN}", stCamOutputInfo.stImage.nFrameNum, currentCameraIdForEvent);
                }


                // 修改后的逻辑：合并条码
                if (allCodeInfosForThisCallback.Any()) 
                {
                    var collectedBarcodes = allCodeInfosForThisCallback.Select(codeInfo => codeInfo.strCode?.TrimEnd('\0')).Where(barcodeString => !string.IsNullOrEmpty(barcodeString)).ToList();

                    if (collectedBarcodes.Any())
                    {
                        string combinedBarcode = string.Join(",", collectedBarcodes);

                        // 新增检查：如果此次条码集合完全包含上次发布条码集合，则跳过
                        if (!string.IsNullOrEmpty(_lastPublishedCombinedBarcode) && !string.IsNullOrEmpty(combinedBarcode))
                        {
                            var lastSet = new HashSet<string>(_lastPublishedCombinedBarcode.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                            var currentSet = new HashSet<string>(combinedBarcode.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                            if (lastSet.All(currentSet.Contains))
                            {
                                Log.Information("[海康工业相机服务] 本次回调条码集合 {CurrentSet} 全部包含上次已发布条码集合 {LastSet}，跳过处理。帧号: {FrameNum}. SN: {SN}",
                                    string.Join("|", currentSet), string.Join("|", lastSet), stCamOutputInfo.stImage.nFrameNum, currentCameraIdForEvent);
                                return; // 直接返回，不处理此次回调的PackageInfo
                            }
                        }

                        var package = PackageInfo.Create();
                        package.SetBarcode(combinedBarcode);
                        package.SetStatus(PackageStatus.Success);
                        package.TriggerTimestamp = DateTime.Now; // 注意：这里的时间戳是当前时间
                        if (finalBitmapSource != null)
                        {
                            package.SetImage(finalBitmapSource, null);
                        }

                        _packageSubject.OnNext(package);

                        // 成功推送后，更新上次发布的条码
                        _lastPublishedCombinedBarcode = combinedBarcode;

                        Log.Information("[海康工业相机服务] 本次回调识别并合并推送 {Count} 个条码为一个事件. 合并后条码: '{CombinedBarcode}'. 帧号: {FrameNum}. SN: {SN}",
                            collectedBarcodes.Count, combinedBarcode, stCamOutputInfo.stImage.nFrameNum, currentCameraIdForEvent);
                    }
                    else
                    {
                        // 如果收集后发现没有有效条码（例如，所有条码都是空或null），则不推送
                        Log.Debug("[海康工业相机服务] 本次回调中未收集到有效条码进行推送. 帧号: {FrameNum}. SN: {SN}", stCamOutputInfo.stImage.nFrameNum, currentCameraIdForEvent);
                    }
                }
                else if (finalBitmapSource != null) // 如果没有条码，但有图像，则可能只推送图像流，不推送PackageInfo
                {
                     // Log.Debug("[海康工业相机服务] 本次回调无条码，但有图像. 帧号: {FrameNum}. SN: {SN}", stCamOutputInfo.stImage.nFrameNum, currentCameraIdForEvent);
                     // 根据现有逻辑，_imageWithIdSubject.OnNext((finalBitmapSource, currentCameraIdForEvent)); 已在前面处理，这里无需额外操作PackageInfo
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[海康工业相机服务] 处理图像回调时发生异常. SN: {SN}", _currentDeviceSerialNumber ?? "N/A");
            }
        }

        private static BitmapSource? ConvertImageDataToBitmapSource(MVIDCodeReader.MVID_IMAGE_INFO imageInfo)
        {
            if (imageInfo.pImageBuf == IntPtr.Zero || imageInfo.nImageLen == 0 || imageInfo.nWidth == 0 || imageInfo.nHeight == 0)
            {
                // Log.Verbose("无效的图像数据传入 ConvertImageDataToBitmapSource。"); // Can be very spammy
                return null;
            }

            try
            {
                var width = (int)imageInfo.nWidth;
                var height = (int)imageInfo.nHeight;
                int stride;
                PixelFormat pf;

                // Copy data from unmanaged memory to managed byte array
                var imageBytes = new byte[imageInfo.nImageLen];
                Marshal.Copy(imageInfo.pImageBuf, imageBytes, 0, (int)imageInfo.nImageLen);

                switch (imageInfo.enImageType)
                {
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_MONO8:
                        pf = PixelFormats.Gray8;
                        stride = width; // For Mono8, stride is typically width
                        break;
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_RGB8_Packed: // Assuming RGB
                        pf = PixelFormats.Rgb24;
                        stride = width * 3;
                        break;
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BGR8_Packed: // Assuming BGR
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BGR24:
                        pf = PixelFormats.Bgr24;
                        stride = width * 3;
                        break;
                     case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_JPEG:
                        using (var ms = new MemoryStream(imageBytes))
                        {
                            var decoder = new JpegBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                            if (decoder.Frames.Count > 0)
                            {
                                var frame = decoder.Frames[0];
                                frame.Freeze(); // Freeze for use on other threads
                                return frame;
                            }
                        }
                        Log.Warning("[海康工业相机服务] 无法从JPEG数据解码图像。");
                        return null;
                    // Add other MVID_IMAGE_TYPE cases as needed, e.g., Bayer formats might require debayering
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_Undefined:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BMP:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_RGB24:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_MONO10:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_MONO10_Packed:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_MONO12:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_MONO12_Packed:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_MONO16:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerGR8:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerRG8:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerGB8:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerBG8:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerGR10:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerRG10:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerGB10:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerBG10:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerGR12:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerRG12:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerGB12:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerBG12:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerGR10_Packed:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerRG10_Packed:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerGB10_Packed:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerBG10_Packed:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerGR12_Packed:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerRG12_Packed:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerGB12_Packed:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BayerBG12_Packed:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_YUV422_Packed:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_YUV422_YUYV_Packed:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_RGBA8_Packed:
                    case MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BGRA8_Packed:
                    default:
                        Log.Warning("[海康工业相机服务] 不支持的 MVID_IMAGE_TYPE 进行直接 BitmapSource 转换: {PixelType}", imageInfo.enImageType);
                        return null;
                }

                // Sanity check for buffer size against calculated stride and height for planar formats
                if (imageInfo.nImageLen < (uint)(stride * height) && 
                    (imageInfo.enImageType == MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_MONO8 ||
                     imageInfo.enImageType == MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_RGB8_Packed ||
                     imageInfo.enImageType == MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BGR8_Packed ||
                     imageInfo.enImageType == MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_BGR24 ))
                {
                    Log.Warning("[海康工业相机服务] 图像数据长度 {DataLen} 小于预期值 {ExpectedLen} (W:{W},H:{H},S:{S},FMT:{FMT}). 无法创建 BitmapSource。",
                        imageInfo.nImageLen, stride * height, width, height, stride, imageInfo.enImageType);
                    return null;
                }


                var bitmap = BitmapSource.Create(
                    width, height,
                    96, 96, // Standard DPI
                    pf,
                    null,    // Palette (null for RGB/Gray8)
                    imageBytes,
                    stride);

                bitmap.Freeze(); // Important for use across threads (e.g., if _imageSubject subscribers are on UI thread)
                return bitmap;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[海康工业相机服务] 图像数据转换为 BitmapSource 时发生异常. PixelType={PixelType}, Width={W}, Height={H}, Len={L}",
                    imageInfo.enImageType, imageInfo.nWidth, imageInfo.nHeight, imageInfo.nImageLen);
                return null;
            }
        }

        // Helper methods to classify barcode TypeIds
        private static bool Is1DBarcodeType(string typeId)
        {
            if (string.IsNullOrWhiteSpace(typeId)) return false;
            // Common 1D barcode type identifiers (case-insensitive)
            var known1DTypes = new[] { "Code128", "Code39", "EAN13", "UPCA", "UPCE", "Codabar", "Interleaved2of5" }; // Add more as needed
            return known1DTypes.Contains(typeId, StringComparer.OrdinalIgnoreCase);
        }

        private static bool Is2DBarcodeType(string typeId)
        {
            if (string.IsNullOrWhiteSpace(typeId)) return false;
            // Common 2D barcode type identifiers (case-insensitive)
            var known2DTypes = new[] { "QRCode", "DataMatrix", "PDF417", "Aztec" }; // Add more as needed
            return known2DTypes.Contains(typeId, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 在给定的 BitmapSource 上绘制条码的边框。
        /// </summary>
        /// <param name="originalImage">原始图像。</param>
        /// <param name="codeInfos">包含条码位置信息的对象列表。</param>
        /// <returns>带有绘制边框的新 BitmapSource，如果失败则返回 null。</returns>
        private static BitmapSource? DrawBarcodeBordersOnImage(BitmapSource originalImage, IEnumerable<MVIDCodeReader.MVID_CODE_INFO> codeInfos)
        {
            var mvidCodeInfos = codeInfos as MVIDCodeReader.MVID_CODE_INFO[] ?? codeInfos.ToArray();
            if (mvidCodeInfos.Length == 0)
            {
                return originalImage; // 如果没有原始图像或没有条码信息，则返回原始图像
            }

            try
            {
                // 确保原始图像已冻结，以便在不同线程上使用（通常在创建时完成）
                if (originalImage is { IsFrozen: false, CanFreeze: true })
                {
                    originalImage.Freeze();
                }

                DrawingVisual drawingVisual = new();
                using (DrawingContext drawingContext = drawingVisual.RenderOpen())
                {
                    // 绘制原始图像
                    drawingContext.DrawImage(originalImage, new Rect(0, 0, originalImage.PixelWidth, originalImage.PixelHeight));

                    Pen greenPen = new(System.Windows.Media.Brushes.LimeGreen, 5); // 使用石灰绿，粗细为2
                    if (greenPen.CanFreeze) greenPen.Freeze();

                    foreach (var codeInfo in mvidCodeInfos)
                    {
                        if (codeInfo.stCornerPt == null || codeInfo.stCornerPt.Length < 4) continue;

                        // 假设 MVID_POINT_I 有公共成员 nX 和 nY
                        // SDK 定义: public struct MVID_POINT_I { public int nX; public int nY; } (假设)
                        // 如果实际名称不同, 则需要修改下面的 .nX 和 .nY
                        System.Windows.Point p1 = new(codeInfo.stCornerPt[0].nX, codeInfo.stCornerPt[0].nY);
                        System.Windows.Point p2 = new(codeInfo.stCornerPt[1].nX, codeInfo.stCornerPt[1].nY);
                        System.Windows.Point p3 = new(codeInfo.stCornerPt[2].nX, codeInfo.stCornerPt[2].nY);
                        System.Windows.Point p4 = new(codeInfo.stCornerPt[3].nX, codeInfo.stCornerPt[3].nY);

                        PathFigure figure = new() { StartPoint = p1, IsClosed = true };
                        figure.Segments.Add(new LineSegment(p2, true));
                        figure.Segments.Add(new LineSegment(p3, true));
                        figure.Segments.Add(new LineSegment(p4, true));
                        // IsClosed = true 会自动从最后一个点连接到 StartPoint
                        if (figure.CanFreeze) figure.Freeze();

                        PathGeometry geometry = new();
                        geometry.Figures.Add(figure);
                        if (geometry.CanFreeze) geometry.Freeze();
                        
                        drawingContext.DrawGeometry(null, greenPen, geometry);
                    }
                }

                RenderTargetBitmap processedBitmap = new(
                    originalImage.PixelWidth, originalImage.PixelHeight,
                    originalImage.DpiX, originalImage.DpiY,
                    PixelFormats.Pbgra32); // 使用支持Alpha和颜色的格式

                processedBitmap.Render(drawingVisual);
                if (processedBitmap.CanFreeze) processedBitmap.Freeze();

                return processedBitmap;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[海康工业相机服务] 在图像上绘制条码边框时发生异常。");
                return null; // 发生错误时返回 null，外部逻辑会使用原始图像
            }
        }

        public void Dispose()
        {
            Log.Debug("[海康工业相机服务] Dispose() 方法被调用. SN: {SN}, HashCode: {HashCode}", _currentDeviceSerialNumber ?? "N/A", GetHashCode());
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposedValue) return;

            Log.Debug("[海康工业相机服务] Dispose({Disposing}) 实际执行. SN: {SN}, HashCode: {HashCode}", disposing, _currentDeviceSerialNumber ?? "N/A", GetHashCode());
            if (disposing)
            {
                // Dispose managed state (managed objects).
                Log.Information("[海康工业相机服务] 正在释放托管资源... SN: {SN}", _currentDeviceSerialNumber ?? "N/A");
                StopInternal(); // Ensure camera is stopped and SDK handle destroyed if active
                _packageSubject.OnCompleted();
                _packageSubject.Dispose();
                _imageWithIdSubject.OnCompleted();
                _imageWithIdSubject.Dispose();
                Log.Information("[海康工业相机服务] 托管资源已释放. SN: {SN}", _currentDeviceSerialNumber ?? "N/A");
            }

            // Free unmanaged resources (unmanaged objects) and override a finalizer below.
            // SDK handles are managed within _device and StopInternal() should take care of MVID_CR_DestroyHandle_NET.
            // No other direct unmanaged resources held by this class.

            _disposedValue = true;
            Log.Information("[海康工业相机服务] 资源释放完成 (Dispose flag: {Disposing}). SN: {SN}", disposing, _currentDeviceSerialNumber ?? "N/A");
        }
        
        ~HikvisionIndustrialCameraService()
        {
            Log.Debug("[海康工业相机服务] Finalizer (~HikvisionIndustrialCameraService) 被调用. SN: {SN}, HashCode: {HashCode}", _currentDeviceSerialNumber ?? "N/A", GetHashCode());
            Dispose(false);
        }
    }
} 
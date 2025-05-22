using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Camera.Interface;
using Camera.Models;
using Common.Models.Package;
using MvVolmeasure.NET;
using Serilog;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Camera.Services.Implementations.Hikvision.Volume
{
    /// <summary>
    /// 海康威视体积测量相机服务。
    /// </summary>
    public sealed class HikvisionVolumeCameraService : ICameraService
    {
        private readonly MvVolmeasure.NET.MvVolmeasure _mvVolmeasure = new();
        private MvVolmeasure.NET.MvVolmeasure.VOLM_DEVICE_INFO_LIST _deviceList;
        
        private readonly Subject<PackageInfo> _packageSubject = new();
        private readonly Subject<(BitmapSource Image, string CameraId)> _imageWithIdSubject = new();
        private readonly Subject<(float Length, float Width, float Height, DateTime Timestamp, bool IsValid, BitmapSource? Image)> _volumeDataWithVerticesSubject = new();


        private string? _currentCameraId;
        private string? _currentDeviceModelName;
        private bool _disposedValue;
        private bool _isGrabbingActive;
        private const int ContinuousWorkMode = 14; 

        private MvVolmeasure.NET.MvVolmeasure.ResultCallback? _resultCallback; // 回调委托字段

        public bool IsConnected { get; private set; }
        public IObservable<PackageInfo> PackageStream => _packageSubject.AsObservable();
        public IObservable<(BitmapSource Image, string CameraId)> ImageStreamWithId => _imageWithIdSubject.AsObservable();
        
        public IObservable<(float Length, float Width, float Height, DateTime Timestamp, bool IsValid, BitmapSource? Image)> VolumeDataWithVerticesStream => _volumeDataWithVerticesSubject.AsObservable();

        public event Action<string?, bool>? ConnectionChanged;

        public HikvisionVolumeCameraService()
        {
            Log.Information("[海康体积相机服务] 实例已创建. HashCode: {HashCode}", GetHashCode());
        }

        private bool EnumerateDevicesInternal()
        {
            _deviceList = new MvVolmeasure.NET.MvVolmeasure.VOLM_DEVICE_INFO_LIST(); 
            var ret = MvVolmeasure.NET.MvVolmeasure.EnumStereoCamEx(
                MvVolmeasure.NET.MvVolmeasure.MV_VOLM_GIGE_DEVICE | MvVolmeasure.NET.MvVolmeasure.MV_VOLM_USB_DEVICE, 
                ref _deviceList);

            if (ret != 0)
            {
                Log.Error("[海康体积相机服务] 枚举设备失败 (EnumStereoCamEx). ErrorCode: {ErrorCode:X8} - {ErrorDesc}", ret, GetHikvisionErrorDescription(ret));
                return false;
            }
            Log.Information("[海康体积相机服务] 枚举到 {DeviceCount} 个海康体积测量设备。", _deviceList.nDeviceNum);

            // 枚举到设备后，详细输出每台设备的关键信息
            for (uint i = 0; i < _deviceList.nDeviceNum; i++)
            {
                try
                {
                    if (_deviceList.pDeviceInfo == null || _deviceList.pDeviceInfo[i] == IntPtr.Zero)
                    {
                        Log.Warning("[海康体积相机服务] 设备信息指针为空，索引: {Index}", i);
                        continue;
                    }

                    var devInfo = Marshal.PtrToStructure<MvVolmeasure.NET.MvVolmeasure.VOLM_DEVICE_INFO>(_deviceList.pDeviceInfo[i]);
                    string typeDesc = devInfo.nTLayerType switch
                    {
                        MvVolmeasure.NET.MvVolmeasure.MV_VOLM_GIGE_DEVICE => "千兆网口",
                        MvVolmeasure.NET.MvVolmeasure.MV_VOLM_USB_DEVICE => "USB3.0",
                        _ => $"未知类型({devInfo.nTLayerType})"
                    };

                    Log.Information("【体积相机】设备[{Index}] 类型: {TypeDesc} 版本: {Major}.{Minor} MAC: {MacHigh:X8}{MacLow:X8}",
                        i, typeDesc, devInfo.nMajorVer, devInfo.nMinorVer, devInfo.nMacAddrHigh, devInfo.nMacAddrLow);

                    if (devInfo.nTLayerType == MvVolmeasure.NET.MvVolmeasure.MV_VOLM_GIGE_DEVICE)
                    {
                        // 解析千兆网口设备详细信息
                        var gigeInfo = (MvVolmeasure.NET.MvVolmeasure.MV_VOLM_GIGE_NET_INFO)
                            MvVolmeasure.NET.MvVolmeasure.ByteToStruct(devInfo.SpecialInfo.stGigEInfo, typeof(MvVolmeasure.NET.MvVolmeasure.MV_VOLM_GIGE_NET_INFO));
                        Log.Information("【体积相机】GIGE详细信息: SN={SN} 型号={Model} 厂商={Vendor} 版本={Ver} 用户名={User} IP={IP:X8} 掩码={Mask:X8} 网关={GW:X8} 厂商自定义={Custom}",
                            gigeInfo.chSerialNumber?.TrimEnd('\0'),
                            gigeInfo.chModelName?.TrimEnd('\0'),
                            gigeInfo.chManufacturerName?.TrimEnd('\0'),
                            gigeInfo.chDeviceVersion?.TrimEnd('\0'),
                            gigeInfo.chUserDefinedName?.TrimEnd('\0'),
                            gigeInfo.nCurrentIp,
                            gigeInfo.nCurrentSubNetMask,
                            gigeInfo.nDefultGateWay,
                            gigeInfo.chManufacturerSpecificInfo?.TrimEnd('\0'));
                    }
                    else if (devInfo.nTLayerType == MvVolmeasure.NET.MvVolmeasure.MV_VOLM_USB_DEVICE)
                    {
                        // 解析USB3.0设备详细信息
                        var usbInfo = (MvVolmeasure.NET.MvVolmeasure.VOLM_USB3_DEVICE_INFO)
                            MvVolmeasure.NET.MvVolmeasure.ByteToStruct(devInfo.SpecialInfo.stUsb3VInfo, typeof(MvVolmeasure.NET.MvVolmeasure.VOLM_USB3_DEVICE_INFO));
                        Log.Information("【体积相机】USB3详细信息: SN={SN} 型号={Model} 厂商={Vendor} 版本={Ver} 用户名={User} VID={VID:X4} PID={PID:X4} 设备号={DevNum} GUID={GUID} 系列={Family} 厂商名={Manu} ",
                            usbInfo.chSerialNumber?.TrimEnd('\0'),
                            usbInfo.chModelName?.TrimEnd('\0'),
                            usbInfo.chVendorName?.TrimEnd('\0'),
                            usbInfo.chDeviceVersion?.TrimEnd('\0'),
                            usbInfo.chUserDefinedName?.TrimEnd('\0'),
                            usbInfo.idVendor,
                            usbInfo.idProduct,
                            usbInfo.nDeviceNumber,
                            usbInfo.chDeviceGUID?.TrimEnd('\0'),
                            usbInfo.chFamilyName?.TrimEnd('\0'),
                            usbInfo.chManufacturerName?.TrimEnd('\0'));
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[海康体积相机服务] 解析设备[{Index}]详细信息时异常", i);
                }
            }

            return true;
        }

        public bool Start()
        {
            if (IsConnected)
            {
                Log.Information("[海康体积相机服务] 服务已连接: {CameraId}", _currentCameraId);
                return true;
            }

            Log.Information("[海康体积相机服务] 正在启动...");

            if (!EnumerateDevicesInternal() || _deviceList.nDeviceNum == 0)
            {
                Log.Warning("[海康体积相机服务] 未找到任何海康体积测量设备，或枚举失败。");
                return false;
            }

            const int deviceIndexToConnect = 1; 
            Log.Information("[海康体积相机服务] 将尝试连接第二个枚举到的设备 (索引 {DeviceIndex})。", deviceIndexToConnect);

            if (deviceIndexToConnect >= _deviceList.nDeviceNum || _deviceList.pDeviceInfo == null || _deviceList.pDeviceInfo[deviceIndexToConnect] == IntPtr.Zero)
            {
                 Log.Error("[海康体积相机服务] 设备索引 {DeviceIndex} 无效或设备信息不可用。", deviceIndexToConnect);
                return false;
            }

            try
            {
                var devInfo = Marshal.PtrToStructure<MvVolmeasure.NET.MvVolmeasure.VOLM_DEVICE_INFO>(_deviceList.pDeviceInfo[deviceIndexToConnect]);
                string? serial = null;
                string? modelName = null;

                switch (devInfo.nTLayerType)
                {
                    case MvVolmeasure.NET.MvVolmeasure.MV_VOLM_GIGE_DEVICE:
                        var gigeInfo = (MvVolmeasure.NET.MvVolmeasure.MV_VOLM_GIGE_NET_INFO)MvVolmeasure.NET.MvVolmeasure.ByteToStruct(devInfo.SpecialInfo.stGigEInfo, typeof(MvVolmeasure.NET.MvVolmeasure.MV_VOLM_GIGE_NET_INFO));
                        serial = gigeInfo.chSerialNumber?.TrimEnd('\0');
                        modelName = gigeInfo.chModelName?.TrimEnd('\0');
                        break;
                    case MvVolmeasure.NET.MvVolmeasure.MV_VOLM_USB_DEVICE:
                        var usbInfo = (MvVolmeasure.NET.MvVolmeasure.VOLM_USB3_DEVICE_INFO)MvVolmeasure.NET.MvVolmeasure.ByteToStruct(devInfo.SpecialInfo.stUsb3VInfo, typeof(MvVolmeasure.NET.MvVolmeasure.VOLM_USB3_DEVICE_INFO));
                        serial = usbInfo.chSerialNumber?.TrimEnd('\0');
                        modelName = usbInfo.chModelName?.TrimEnd('\0');
                        break;
                }

                if (string.IsNullOrEmpty(serial))
                {
                    Log.Error("[海康体积相机服务] 设备序列号为空，无法连接。设备索引: {DeviceIndex}", deviceIndexToConnect);
                    return false;
                }

                var ret = _mvVolmeasure.CreateHandleBySerial(serial);
                if (ret != 0)
                {
                    Log.Error("[海康体积相机服务] 连接设备失败 (CreateHandleBySerial). SN: {SerialNumber}, ErrorCode: {ErrorCode:X8} - {ErrorDesc}", serial, ret, GetHikvisionErrorDescription(ret));
                    return false;
                }

                _currentCameraId = serial;
                _currentDeviceModelName = modelName ?? "VolumeCamera";
                IsConnected = true;
                ConnectionChanged?.Invoke(_currentCameraId, true);
                Log.Information("[海康体积相机服务] 设备连接成功: SN={SerialNumber}, Model={ModelName}", _currentCameraId, _currentDeviceModelName);

                // Set algorithm type for continuous measurement
                ret = _mvVolmeasure.SetAlgorithmType(ContinuousWorkMode);
                if (ret != 0)
                {
                    Log.Error("[海康体积相机服务] 设置算法类型为持续模式失败. ErrorCode: {ErrorCode:X8} - {ErrorDesc}", ret, GetHikvisionErrorDescription(ret));
                    // Attempt to clean up
                    _mvVolmeasure.DeInit();
                    IsConnected = false;
                    ConnectionChanged?.Invoke(_currentCameraId, false);
                    _currentCameraId = null;
                    return false;
                }
                Log.Debug("[海康体积相机服务] 算法类型已设置为持续测量模式 ({ContinuousWorkMode}).", ContinuousWorkMode);
                
                // 注册回调函数
                _resultCallback = OnResultCallback; // 委托实例化
                ret = _mvVolmeasure.RegisterResultCallBack(_resultCallback, IntPtr.Zero);
                if (ret != 0)
                {
                    Log.Error("[海康体积相机服务] 注册回调失败. ErrorCode: {ErrorCode:X8} - {ErrorDesc}", ret, GetHikvisionErrorDescription(ret));
                    _mvVolmeasure.DeInit();
                    IsConnected = false;
                    ConnectionChanged?.Invoke(_currentCameraId, false);
                    _currentCameraId = null;
                    return false;
                }
                Log.Debug("[海康体积相机服务] 回调注册成功.");

                ret = _mvVolmeasure.StartGrabbing();
                if (ret != 0)
                {
                    Log.Error("[海康体积相机服务] 开始抓图失败 (StartGrabbing). ErrorCode: {ErrorCode:X8} - {ErrorDesc}", ret, GetHikvisionErrorDescription(ret));
                     _mvVolmeasure.DeInit(); // Clean up
                    IsConnected = false;
                    ConnectionChanged?.Invoke(_currentCameraId, false);
                    _currentCameraId = null;
                    return false;
                }
                Log.Debug("[海康体积相机服务] 抓图已启动 (StartGrabbing).\n");

                ret = _mvVolmeasure.StartMeasure();
                if (ret != 0)
                {
                    Log.Error("[海康体积相机服务] 开始测量失败 (StartMeasure). ErrorCode: {ErrorCode:X8} - {ErrorDesc}", ret, GetHikvisionErrorDescription(ret));
                    _mvVolmeasure.StopGrabbing();
                    _mvVolmeasure.DeInit();
                    IsConnected = false;
                    ConnectionChanged?.Invoke(_currentCameraId, false);
                    _currentCameraId = null;
                    return false;
                }
                _isGrabbingActive = true;
                Log.Information("[海康体积相机服务] 服务启动成功，已开始抓图和测量. SN={SerialNumber}", _currentCameraId);

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[海康体积相机服务] 启动服务时发生异常.");
                StopInternal(false); // Ensure cleanup on failure
                return false;
            }
        }

        public bool Stop()
        {
           return StopInternal(true);
        }

        private bool StopInternal(bool performDeinit)
        {
            string? disconnectedSn = _currentCameraId;
            Log.Information("[海康体积相机服务] 正在停止服务... 当前连接: {CameraId}", disconnectedSn ?? "N/A");

            if (!IsConnected && !_isGrabbingActive)
            {
                Log.Information("[海康体积相机服务] 服务已停止或未初始化。");
                return true;
            }
            
            bool deinitPerformedSuccessfully = true;

            try
            {
                if (_isGrabbingActive)
                {
                    var retStopGrab = _mvVolmeasure.StopGrabbing(); 
                    if (retStopGrab != 0)
                    {
                        Log.Warning("[海康体积相机服务] 停止抓图/测量失败 (StopGrabbing). SN: {SN}, ErrorCode: {ErrorCode:X8} - {ErrorDesc}", disconnectedSn, retStopGrab, GetHikvisionErrorDescription(retStopGrab));
                    }
                    else
                    {
                        Log.Information("[海康体积相机服务] 抓图/测量已停止 (StopGrabbing). SN: {SN}", disconnectedSn);
                    }
                    _isGrabbingActive = false;
                }

                if (performDeinit && _mvVolmeasure.GetHashCode() != 0 && IsConnected)
                {
                    Log.Information("[海康体积相机服务] 正在反初始化 SDK 句柄... SN: {SN}", disconnectedSn);
                    var retDeInit = _mvVolmeasure.DeInit();
                    if (retDeInit != 0)
                    {
                        Log.Error("[海康体积相机服务] 反初始化 SDK 句柄失败 (DeInit). SN: {SN}, ErrorCode: {ErrorCode:X8} - {ErrorDesc}", disconnectedSn, retDeInit, GetHikvisionErrorDescription(retDeInit));
                        deinitPerformedSuccessfully = false;
                    }
                    else
                    {
                        Log.Information("[海康体积相机服务] SDK 句柄已反初始化. SN: {SN}", disconnectedSn);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[海康体积相机服务] 停止服务时发生异常. SN: {SN}", disconnectedSn);
                deinitPerformedSuccessfully = false;
            }
            finally
            {
                if (IsConnected)
                {
                    IsConnected = false;
                    ConnectionChanged?.Invoke(disconnectedSn, false);
                    Log.Information("[海康体积相机服务] 设备连接已断开: {CameraId}", disconnectedSn ?? "N/A");
                }
                _currentCameraId = null;
                _currentDeviceModelName = null;
            }
            Log.Information("[海康体积相机服务] 服务已停止。");
            return deinitPerformedSuccessfully;
        }

        #region SDK Data Conversion and Extraction (Adapted from DeviceService)

        private static (float Length, float Width, float Height, bool IsValid, BitmapSource? Image, Point[]? VertexPoints) ExtractMeasurementData(VOLM_RESULT_INFO stResultInfo)
        {
            float length = 0, width = 0, height = 0;
            bool isValidMeasurement;
            BitmapSource? image = null;
            Point[]? vertexPoints = null;

            if (stResultInfo.nVolumeFlag == 1)
            {
                var volInfo = stResultInfo.stVolumeInfo;

                if (volInfo is { length: > 0, width: > 0, height: > 0 })
                {
                    length = volInfo.length;
                    width = volInfo.width;
                    height = volInfo.height;
                    isValidMeasurement = true;

                    if (volInfo.vertex_pnts is { Length: > 0 })
                    {
                        vertexPoints = [.. volInfo.vertex_pnts.Select(sdkPoint => new Point(sdkPoint.fX, sdkPoint.fY))];
                        if(vertexPoints.Length == 0) vertexPoints = null;

                        // 添加日志记录
                        if (vertexPoints != null && image != null) // 确保 image 也不是 null
                        {
                            var pointsStr = string.Join(", ", vertexPoints.Select(p => $"({{X={p.X:F2},Y={p.Y:F2}}})"));
                            Log.Debug("[海康体积相机服务] ExtractMeasurementData: 提取的顶点坐标: [{VertexPointsStr}]. 关联图像尺寸: {W}x{H}", pointsStr, image.PixelWidth, image.PixelHeight);
                        }
                        else if (vertexPoints != null)
                        {
                            var pointsStr = string.Join(", ", vertexPoints.Select(p => $"({{X={p.X:F2},Y={p.Y:F2}}})"));
                            Log.Debug("[海康体积相机服务] ExtractMeasurementData: 提取的顶点坐标: [{VertexPointsStr}]. 关联图像为 null。", pointsStr);
                        }
                    }
                }
                else
                {
                     Log.Warning("[海康体积相机服务] ExtractMeasurementData: nVolumeFlag=1 但尺寸无效或为零. L:{L}, W:{W}, H:{H}",
                                volInfo.length, volInfo.width, volInfo.height);
                    isValidMeasurement = false;
                }
            }
            else
            {
                Log.Debug("[海康体积相机服务] ExtractMeasurementData: nVolumeFlag is {VolumeFlag}, 表示无有效体积数据。", stResultInfo.nVolumeFlag);
                isValidMeasurement = false;
            }

            if (stResultInfo.nImgFlag == 1)
            {
                if (stResultInfo.stExtendImage.nDataLen > 0 && stResultInfo.stExtendImage.pData != IntPtr.Zero)
                {
                    var extendedFrame = ConvertExtendToFrameInfo(stResultInfo.stExtendImage);
                    image = ConvertFrameToBitmapSource(extendedFrame);
                    if (image != null && vertexPoints == null) // 如果只有图像没有顶点，也记录一下图像尺寸
                    {
                        Log.Debug("[海康体积相机服务] ExtractMeasurementData: 提取到图像但无顶点数据. 图像尺寸: {W}x{H}", image.PixelWidth, image.PixelHeight);
                    }
                }
                if (image == null && stResultInfo.stImage.nFrameLen > 0 && stResultInfo.stImage.pData != IntPtr.Zero)
                {
                    image = ConvertFrameToBitmapSource(stResultInfo.stImage);
                    if (image != null && vertexPoints == null) // 如果只有图像没有顶点，也记录一下图像尺寸
                    {
                         Log.Debug("[海康体积相机服务] ExtractMeasurementData: 提取到图像但无顶点数据. 图像尺寸: {W}x{H}", image.PixelWidth, image.PixelHeight);
                    }
                }
            }
            else
            {
                Log.Debug("[海康体积相机服务] ExtractMeasurementData: nImgFlag is {ImgFlag}, 无图像数据。", stResultInfo.nImgFlag);
            }

            switch (isValidMeasurement)
            {
                case true when image != null && vertexPoints is { Length: > 0 }:
                {
                    var imageWithBorder = DrawRedBorderOnBitmapSource(image, vertexPoints);
                    image = imageWithBorder;
                    break;
                }
                case false:
                    vertexPoints = null;
                    break;
            }

            return (length, width, height, isValidMeasurement, image, vertexPoints);
        }

        private static BitmapSource DrawRedBorderOnBitmapSource(BitmapSource originalImage, Point[] vertices)
        {
            if (vertices.Length == 0) return originalImage;

            // 坐标变换：将中心原点坐标转为左上角原点坐标
            var correctedVertices = vertices.Select(v =>
                new Point(v.X + originalImage.PixelWidth / 2.0, v.Y + originalImage.PixelHeight / 2.0)
            ).ToArray();

            DrawingVisual drawingVisual = new();
            using (DrawingContext drawingContext = drawingVisual.RenderOpen())
            {
                drawingContext.DrawImage(originalImage, new Rect(0, 0, originalImage.PixelWidth, originalImage.PixelHeight));

                Pen redPen = new(Brushes.Red, 10);
                redPen.Freeze();

                if (correctedVertices.Length > 1)
                {
                    for (int i = 0; i < correctedVertices.Length - 1; i++)
                    {
                        drawingContext.DrawLine(redPen, correctedVertices[i], correctedVertices[i + 1]);
                    }
                    drawingContext.DrawLine(redPen, correctedVertices[^1], correctedVertices[0]);
                }
            }

            RenderTargetBitmap borderedBitmap = new RenderTargetBitmap(
                originalImage.PixelWidth, originalImage.PixelHeight,
                originalImage.DpiX, originalImage.DpiY, PixelFormats.Pbgra32);
            borderedBitmap.Render(drawingVisual);
            borderedBitmap.Freeze();
            return borderedBitmap;
        }

        private static VOLM_FRAME_INFO ConvertExtendToFrameInfo(VOLM_EXTEND_INFO ext)
        {
            return new VOLM_FRAME_INFO
            {
                enPixelType = (uint)ext.enPixelType, // Cast might be needed if enPixelType is an enum in VOLM_EXTEND_INFO
                nFrameLen = ext.nDataLen,
                nHeight = (ushort)ext.nHeight,
                nWidth = (ushort)ext.nWidth,
                pData = ext.pData,
                // Other fields like nTimeStampHigh/Low might need to be mapped if used
            };
        }

        private static BitmapSource? ConvertFrameToBitmapSource(VOLM_FRAME_INFO frame)
        {
            if (frame.pData == IntPtr.Zero || frame.nFrameLen == 0 || frame.nWidth == 0 || frame.nHeight == 0)
            {
                Log.Verbose("[海康体积相机服务] 无效的图像帧数据传入 ConvertFrameToBitmapSource。");
                return null;
            }

            try
            {
                // Ensure data is copied to a managed array
                byte[] imageData = new byte[frame.nFrameLen];
                Marshal.Copy(frame.pData, imageData, 0, (int)frame.nFrameLen);

                switch ((VOLM_PIXEL_TYPE)frame.enPixelType) // Assuming VOLM_PIXEL_TYPE is an enum
                {
                    case VOLM_PIXEL_TYPE.PIXEL_TYPE_RGB8_PLANAR: // This might be PIXEL_TYPE_BGR8_PACKED or similar
                        var pf = PixelFormats.Rgb24;
                        var stride = frame.nWidth * 3; // Default for many formats
                         if (frame.nFrameLen < stride * frame.nHeight) {
                            Log.Warning($"[海康体积相机服务] 图像帧数据长度 {frame.nFrameLen} 不足以满足预期 RGB24 格式 ({stride * frame.nHeight}).");
                            //return null; // Keep original behavior for now, but log it. The new planar conversion below has its own check.
                        }
                        
                        // 正确处理 RGB8 Planar 格式
                        int planeSize = frame.nWidth * frame.nHeight;
                        if (frame.nFrameLen < planeSize * 3)
                        {
                            Log.Warning($"[海康体积相机服务] 图像帧数据长度 {frame.nFrameLen} 不足以满足 RGB8 Planar 格式 ({frame.nWidth}x{frame.nHeight}x3 = {planeSize * 3}).");
                            return null;
                        }

                        // imageData 已经包含了 Marshal.Copy 过来的数据
                        // byte[] planarData = imageData; // imageData is already the planar data

                        byte[] interleavedRgbData = new byte[planeSize * 3];

                        for (int y = 0; y < frame.nHeight; y++)
                        {
                            for (int x = 0; x < frame.nWidth; x++)
                            {
                                int planarPixelIndex = y * frame.nWidth + x; // 单个平面内的像素索引
                                int interleavedPixelStartIndex = planarPixelIndex * 3; // 交错数据中此像素的起始索引

                                // 假设平面顺序为 R, G, B
                                interleavedRgbData[interleavedPixelStartIndex + 0] = imageData[planarPixelIndex];                 // R from R-plane
                                interleavedRgbData[interleavedPixelStartIndex + 1] = imageData[planarPixelIndex + planeSize];     // G from G-plane
                                interleavedRgbData[interleavedPixelStartIndex + 2] = imageData[planarPixelIndex + planeSize * 2]; // B from B-plane
                            }
                        }
                        
                        // 使用转换后的交错数据创建 BitmapSource
                        var planarBitmap = BitmapSource.Create(
                            frame.nWidth,
                            frame.nHeight,
                            96, 96, // Standard DPI
                            pf,     // PixelFormats.Rgb24 for the interleavedRgbData
                            null,   // Palette
                            interleavedRgbData, // 使用正确格式化的数据
                            stride  // 交错数据的正确步长
                        );
                        planarBitmap.Freeze();
                        return planarBitmap;
                        // break; // Unreachable after return
                    default:
                        Log.Warning("[海康体积相机服务] 不支持的像素类型转换: {PixelType}", (VOLM_PIXEL_TYPE)frame.enPixelType);
                        return null;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[海康体积相机服务] 图像数据转换为 BitmapSource 时异常. PixelType={PixelType}, W={W}, H={H}, Len={L}",
                          (VOLM_PIXEL_TYPE)frame.enPixelType, frame.nWidth, frame.nHeight, frame.nFrameLen);
                return null;
            }
        }
        #endregion

        #region Error Code Description
        private static string GetHikvisionErrorDescription(int errorCode)
        {
            return errorCode switch
            {
                0x00000000 => "成功 (MV_VOLM_OK)",
                0x10000000 => "算法库：不确定类型错误 (MV_VOLM_E_ALG)",
                0x10000001 => "算法库：ABILITY存在无效参数 (MV_VOLM_E_ABILITY_ARG)",
                0x10000002 => "算法库：内存地址为空 (MV_VOLM_E_MEM_NULL)",
                unchecked((int)0x80011000) => "SDK：错误或无效的句柄 (MV_VOLM_E_HANDLE)",
                unchecked((int)0x80011001) => "SDK：不支持的功能 (MV_VOLM_E_SUPPORT)",
                unchecked((int)0x80011004) => "SDK：错误的参数 (MV_VOLM_E_PARAMETER)",
                unchecked((int)0x80011006) => "SDK：超时 (MV_VOLM_E_TIMEOUT)",
                unchecked((int)0x80011007) => "SDK：无数据 (MV_VOLM_E_NODATA)",
                unchecked((int)0x80000100) => "GenICam：通用错误 (MV_VOLM_E_GC_GENERIC)",
                unchecked((int)0x80000204) => "GigE：设备忙或网络断开 (MV_VOLM_E_BUSY)",
                _ => $"未知错误 (0x{errorCode:X8})",
            };
        }
        #endregion

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposedValue) return;

            Log.Debug("[海康体积相机服务] Dispose({Disposing}) 执行. SN: {SN}, HashCode: {HashCode}", disposing, _currentCameraId ?? "N/A", GetHashCode());
            if (disposing)
            {
                Log.Information("[海康体积相机服务] 正在释放托管资源... SN: {SN}", _currentCameraId ?? "N/A");
                StopInternal(true);

                _packageSubject.OnCompleted();
                _packageSubject.Dispose();
                _imageWithIdSubject.OnCompleted();
                _imageWithIdSubject.Dispose();
                _volumeDataWithVerticesSubject.OnCompleted();
                _volumeDataWithVerticesSubject.Dispose();
                Log.Information("[海康体积相机服务] 托管资源已释放. SN: {SN}", _currentCameraId ?? "N/A");
            }

            _disposedValue = true;
            Log.Information("[海康体积相机服务] 资源释放完成 (Dispose flag: {Disposing}). SN: {SN}", disposing, _currentCameraId ?? "N/A");
        }
        
        ~HikvisionVolumeCameraService()
        {
            Log.Debug("[海康体积相机服务] Finalizer (~HikvisionVolumeCameraService) 被调用. SN: {SN}, HashCode: {HashCode}", _currentCameraId ?? "N/A", GetHashCode());
            Dispose(false);
        }
        
        public (bool isSuccess, float length, float width, float height, string? errorMessage) TriggerMeasure(int workMode = 0, int timeoutMs = 2000)
        {
            Log.Warning("[海康体积相机服务] TriggerMeasure 被调用，但此相机主要用于持续测量模式。");
            if (!IsConnected || !_isGrabbingActive)
            {
                return (false, 0,0,0, "相机未连接或未在主动测量模式。");
            }
            
            Log.Information("[海康体积相机服务] TriggerMeasure: 依赖于持续回调提供数据，不执行主动触发。请订阅 VolumeDataWithVerticesStream 或 PackageStream。");
             return (false, 0,0,0, "此相机工作在持续测量模式，请从数据流获取结果。");
        }

        /// <summary>
        /// 主动获取一次测量结果。
        /// 注意：此相机通常在持续测量模式下运行，并通过回调提供数据。
        /// 此方法直接调用 SDK 的 GetResult，可能返回最新的缓存结果或在特定配置下工作。
        /// </summary>
        /// <returns>
        /// 一个元组，包含SDK调用是否成功、测量尺寸、测量是否有效、图像、顶点和错误消息。
        /// </returns>
        public (bool IsSdkCallSuccess, float Length, float Width, float Height, bool IsMeasurementValid, BitmapSource? Image, Point[]? VertexPoints, string? ErrorMessage) GetSingleMeasurement()
        {
            Log.Information("[海康体积相机服务] GetSingleMeasurement 被调用。SN: {SN}", _currentCameraId ?? "N/A");

            if (!IsConnected)
            {
                Log.Warning("[海康体积相机服务] GetSingleMeasurement: 相机未连接。");
                return (false, 0, 0, 0, false, null, null, "相机未连接。");
            }
            if (!_isGrabbingActive)
            {
                 Log.Warning("[海康体积相机服务] GetSingleMeasurement: 相机未处于抓图/测量状态 (isGrabbingActive is false)。");
                return (false, 0, 0, 0, false, null, null, "相机未处于抓图/测量状态。");
            }

            var stResultInfo = new VOLM_RESULT_INFO();
            try
            {
                var ret = _mvVolmeasure.GetResult(ref stResultInfo);
                if (ret != 0)
                {
                    string errorDesc = GetHikvisionErrorDescription(ret);
                    Log.Error("[海康体积相机服务] GetSingleMeasurement: SDK GetResult 失败. SN: {SN}, ErrorCode: {ErrorCode:X8} - {ErrorDesc}", _currentCameraId, ret, errorDesc);
                    return (false, 0, 0, 0, false, null, null, $"SDK GetResult 失败: {errorDesc} (Code: 0x{ret:X8})");
                }

                Log.Debug("[海康体积相机服务] GetSingleMeasurement: SDK GetResult 成功. SN: {SN}. VolumeFlag: {VolFlag}, ImgFlag: {ImgFlag}", 
                          _currentCameraId, stResultInfo.nVolumeFlag, stResultInfo.nImgFlag);

                var (length, width, height, isValid, image, vertexPoints) = ExtractMeasurementData(stResultInfo);

                if (isValid)
                {
                     Log.Information("[海康体积相机服务] GetSingleMeasurement: 提取到有效测量: L={L} W={W} H={H}. SN={SN}. Image: {HasImage}. Vertices: {HasVertices}",
                                    length, width, height, _currentCameraId, image != null, vertexPoints is
                                    {
                                        Length: > 0
                                    });
                }
                else
                {
                    Log.Warning("[海康体积相机服务] GetSingleMeasurement: 提取到的测量数据无效. SN={SN}", _currentCameraId);
                }
                return (true, length, width, height, isValid, image, vertexPoints, null);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[海康体积相机服务] GetSingleMeasurement: 调用过程中发生异常. SN: {SN}", _currentCameraId);
                return (false, 0, 0, 0, false, null, null, $"处理 GetSingleMeasurement 时发生异常: {ex.Message}");
            }
        }

        // 新增：SDK回调处理方法
        private void OnResultCallback(ref VOLM_RESULT_INFO stResultInfo, IntPtr pUser)
        {
            // 解析数据并推送到流
            var (length, width, height, isValid, image, _) = ExtractMeasurementData(stResultInfo);
            var timestamp = DateTime.Now;
            _volumeDataWithVerticesSubject.OnNext((length, width, height, timestamp, isValid, image));
            if (image != null)
            {
                _imageWithIdSubject.OnNext((image, _currentCameraId ?? "Unknown"));
            }
        }
    }
} 
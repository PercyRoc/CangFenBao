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
        
        private readonly MvVolmeasure.NET.MvVolmeasure.ResultCallback? _persistentResultCallback;
        private const int ContinuousWorkMode = 14; 

        public bool IsConnected { get; private set; }
        public IObservable<PackageInfo> PackageStream => _packageSubject.AsObservable();
        public IObservable<(BitmapSource Image, string CameraId)> ImageStreamWithId => _imageWithIdSubject.AsObservable();
        
        public IObservable<(float Length, float Width, float Height, DateTime Timestamp, bool IsValid, BitmapSource? Image)> VolumeDataWithVerticesStream => _volumeDataWithVerticesSubject.AsObservable();

        public event Action<string?, bool>? ConnectionChanged;

        public HikvisionVolumeCameraService()
        {
            _persistentResultCallback = HandlePersistentResultCallback;
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
            return true;
        }

        public IEnumerable<CameraInfo> GetAvailableCameras()
        {
            var availableCameras = new List<CameraInfo>();
             var localDeviceList = new MvVolmeasure.NET.MvVolmeasure.VOLM_DEVICE_INFO_LIST();
            var nRet = MvVolmeasure.NET.MvVolmeasure.EnumStereoCamEx(
                MvVolmeasure.NET.MvVolmeasure.MV_VOLM_GIGE_DEVICE | MvVolmeasure.NET.MvVolmeasure.MV_VOLM_USB_DEVICE, 
                ref localDeviceList);

            if (nRet != 0)
            {
                Log.Error("[海康体积相机服务] (GetAvailable) 枚举设备失败. ErrorCode: {ErrorCode:X8} - {ErrorDesc}", nRet, GetHikvisionErrorDescription(nRet));
                return availableCameras;
            }

            Log.Debug("[海康体积相机服务] (GetAvailable) 枚举到 {DeviceCount} 台设备。", localDeviceList.nDeviceNum);

            for (uint i = 0; i < localDeviceList.nDeviceNum; i++)
            {
                if (localDeviceList.pDeviceInfo == null || localDeviceList.pDeviceInfo[i] == IntPtr.Zero)
                {
                    Log.Warning("[海康体积相机服务] (GetAvailable) 设备信息指针为空，索引: {Index}", i);
                    continue;
                }
                try
                {
                    var devInfo = Marshal.PtrToStructure<MvVolmeasure.NET.MvVolmeasure.VOLM_DEVICE_INFO>(localDeviceList.pDeviceInfo[i]);
                    string id = $"VolumeCam_{i}";
                    string? name = $"海康体积相机 {i}";
                    string? model = "未知型号";
                    string? serial = null;

                    switch (devInfo.nTLayerType)
                    {
                        case MvVolmeasure.NET.MvVolmeasure.MV_VOLM_GIGE_DEVICE:
                        {
                            var gigeInfo = (MvVolmeasure.NET.MvVolmeasure.MV_VOLM_GIGE_NET_INFO)
                                MvVolmeasure.NET.MvVolmeasure.ByteToStruct(devInfo.SpecialInfo.stGigEInfo, typeof(MvVolmeasure.NET.MvVolmeasure.MV_VOLM_GIGE_NET_INFO));
                            serial = gigeInfo.chSerialNumber?.TrimEnd('\0');
                            name = gigeInfo.chUserDefinedName?.TrimEnd('\0');
                            if (string.IsNullOrWhiteSpace(name)) name = gigeInfo.chModelName?.TrimEnd('\0');
                            model = gigeInfo.chModelName?.TrimEnd('\0');
                            if (!string.IsNullOrEmpty(serial)) id = serial;
                            break;
                        }
                        case MvVolmeasure.NET.MvVolmeasure.MV_VOLM_USB_DEVICE:
                        {
                            var usbInfo = (MvVolmeasure.NET.MvVolmeasure.VOLM_USB3_DEVICE_INFO)
                                MvVolmeasure.NET.MvVolmeasure.ByteToStruct(devInfo.SpecialInfo.stUsb3VInfo, typeof(MvVolmeasure.NET.MvVolmeasure.VOLM_USB3_DEVICE_INFO));
                            serial = usbInfo.chSerialNumber?.TrimEnd('\0');
                            name = usbInfo.chUserDefinedName?.TrimEnd('\0');
                            if (string.IsNullOrWhiteSpace(name)) name = usbInfo.chModelName?.TrimEnd('\0');
                            model = usbInfo.chModelName?.TrimEnd('\0');
                            if (!string.IsNullOrEmpty(serial)) id = serial;
                            break;
                        }
                    }
                    
                    string displayName = string.IsNullOrWhiteSpace(name) ? (serial ?? id) : name;
                    var cameraStatus = (_currentCameraId == serial && IsConnected) ? "已连接" : "未连接";

                    availableCameras.Add(new CameraInfo
                    {
                        Id = id,
                        Name = displayName,
                        Model = model ?? "N/A",
                        SerialNumber = serial ?? "N/A",
                        Status = cameraStatus
                    });
                    Log.Verbose("[海康体积相机服务] (GetAvailable) 发现设备: ID={Id}, Name={Name}, Model={Model}, SN={SN}, Status={Status}",
                                id, displayName, model, serial, cameraStatus);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[海康体积相机服务] (GetAvailable) 处理设备信息时出错，索引: {Index}", i);
                }
            }
            return availableCameras;
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
                
                // Register persistent callback
                if (_persistentResultCallback != null)
                {
                     ret = _mvVolmeasure.RegisterResultCallBack(_persistentResultCallback, IntPtr.Zero);
                    if (ret != 0)
                    {
                        Log.Error("[海康体积相机服务] 注册持续结果回调失败. ErrorCode: {ErrorCode:X8} - {ErrorDesc}", ret, GetHikvisionErrorDescription(ret));
                        _mvVolmeasure.DeInit();
                        IsConnected = false;
                        ConnectionChanged?.Invoke(_currentCameraId, false);
                        _currentCameraId = null;
                        return false;
                    }
                    Log.Debug("[海康体积相机服务] 持续结果回调注册成功.");
                }
                else
                {
                    Log.Error("[海康体积相机服务] 持续结果回调委托为空，无法注册!");
                     _mvVolmeasure.DeInit();
                     IsConnected = false;
                    ConnectionChanged?.Invoke(_currentCameraId, false);
                    _currentCameraId = null;
                    return false;
                }


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
                Log.Debug("[海康体积相机服务] 抓图已启动 (StartGrabbing).");

                ret = _mvVolmeasure.StartMeasure();
                if (ret != 0)
                {
                    Log.Error("[海康体积相机服务] 开始测量失败 (StartMeasure). ErrorCode: {ErrorCode:X8} - {ErrorDesc}", ret, GetHikvisionErrorDescription(ret));
                    _mvVolmeasure.StopGrabbing(); // Attempt to stop grabbing
                    _mvVolmeasure.DeInit();       // Clean up
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

        private void HandlePersistentResultCallback(ref VOLM_RESULT_INFO stResultInfo, IntPtr pUser)
        {
            if (!_isGrabbingActive || _disposedValue) return;
            if (string.IsNullOrEmpty(_currentCameraId))
            {
                Log.Warning("[海康体积相机服务] 回调触发，但当前相机ID为空，忽略数据。");
                return;
            }

            var timestamp = DateTime.Now;
            Log.Debug("[海康体积相机服务] 持续测量回调数据接收. SN={SN}, Timestamp={Timestamp}", _currentCameraId, timestamp.ToString("HH:mm:ss.fff"));

            try
            {
                var (length, width, height, isValid, image, vertexPoints) = ExtractMeasurementData(stResultInfo);

                _volumeDataWithVerticesSubject.OnNext((length, width, height, timestamp, isValid, image));

                if (isValid)
                {
                    Log.Information("[海康体积相机服务] 有效测量: L={L} W={W} H={H}. SN={SN}. Image: {HasImage}. Vertices: {HasVertices}",
                                    length, width, height, _currentCameraId, image != null, vertexPoints is
                                    {
                                        Length: > 0
                                    });

                    if (image != null)
                    {
                        _imageWithIdSubject.OnNext((image, _currentCameraId));
                    }
                }
                else
                {
                    Log.Warning("[海康体积相机服务] 无效测量数据. SN={SN}. VolumeFlag: {VolFlag}, ImgFlag: {ImgFlag}", 
                                _currentCameraId, stResultInfo.nVolumeFlag, stResultInfo.nImgFlag);
                    if (image != null)
                    {
                         _imageWithIdSubject.OnNext((image, _currentCameraId));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[海康体积相机服务] 处理持续测量回调时发生异常. SN={SN}", _currentCameraId);
            }
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
                        vertexPoints = volInfo.vertex_pnts
                            .Select(sdkPoint => new Point(sdkPoint.fX, sdkPoint.fY))
                            .ToArray();
                        if(vertexPoints.Length == 0) vertexPoints = null;
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
                }
                if (image == null && stResultInfo.stImage.nFrameLen > 0 && stResultInfo.stImage.pData != IntPtr.Zero)
                {
                    image = ConvertFrameToBitmapSource(stResultInfo.stImage);
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

            DrawingVisual drawingVisual = new DrawingVisual();
            using (DrawingContext drawingContext = drawingVisual.RenderOpen())
            {
                drawingContext.DrawImage(originalImage, new Rect(0, 0, originalImage.PixelWidth, originalImage.PixelHeight));

                Pen redPen = new Pen(Brushes.Red, 2); // Thickness 2
                redPen.Freeze();

                if (vertices.Length >= 2) 
                {
                    if (vertices.Length == 8) // Assume 8 points for a cuboid
                    {
                        // Draw "bottom" face
                        drawingContext.DrawLine(redPen, vertices[0], vertices[1]);
                        drawingContext.DrawLine(redPen, vertices[1], vertices[2]);
                        drawingContext.DrawLine(redPen, vertices[2], vertices[3]);
                        drawingContext.DrawLine(redPen, vertices[3], vertices[0]);

                        // Draw "top" face
                        drawingContext.DrawLine(redPen, vertices[4], vertices[5]);
                        drawingContext.DrawLine(redPen, vertices[5], vertices[6]);
                        drawingContext.DrawLine(redPen, vertices[6], vertices[7]);
                        drawingContext.DrawLine(redPen, vertices[7], vertices[4]);

                        // Draw connecting edges
                        drawingContext.DrawLine(redPen, vertices[0], vertices[4]);
                        drawingContext.DrawLine(redPen, vertices[1], vertices[5]);
                        drawingContext.DrawLine(redPen, vertices[2], vertices[6]);
                        drawingContext.DrawLine(redPen, vertices[3], vertices[7]);
                    }
                    else if (vertices.Length == 4) // Assume 4 points for a rectangle
                    {
                        drawingContext.DrawLine(redPen, vertices[0], vertices[1]);
                        drawingContext.DrawLine(redPen, vertices[1], vertices[2]);
                        drawingContext.DrawLine(redPen, vertices[2], vertices[3]);
                        drawingContext.DrawLine(redPen, vertices[3], vertices[0]);
                    }
                    else // Fallback: connect all points sequentially
                    {
                         for (int i = 0; i < vertices.Length - 1; i++)
                         {
                             drawingContext.DrawLine(redPen, vertices[i], vertices[i+1]);
                         }
                         if (vertices.Length > 1) // Connect last to first if more than one point
                         {
                             drawingContext.DrawLine(redPen, vertices[^1], vertices[0]);
                         }
                    }
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
                PixelFormat pf;
                int stride; // Default for many formats

                // Ensure data is copied to a managed array
                byte[] imageData = new byte[frame.nFrameLen];
                Marshal.Copy(frame.pData, imageData, 0, (int)frame.nFrameLen);

                switch ((VOLM_PIXEL_TYPE)frame.enPixelType) // Assuming VOLM_PIXEL_TYPE is an enum
                {
                    case VOLM_PIXEL_TYPE.PIXEL_TYPE_RGB8_PLANAR: // This might be PIXEL_TYPE_BGR8_PACKED or similar
                        pf = PixelFormats.Rgb24; // Or Bgr24 if SDK uses BGR order
                        stride = frame.nWidth * 3;
                         if (frame.nFrameLen < stride * frame.nHeight) {
                            Log.Warning($"Frame length {frame.nFrameLen} is less than expected {stride * frame.nHeight} for RGB24.");
                            return null;
                        }
                        break;
                    default:
                        Log.Warning("[海康体积相机服务] 不支持的像素类型转换: {PixelType}", (VOLM_PIXEL_TYPE)frame.enPixelType);
                        return null;
                }

                var bitmap = BitmapSource.Create(
                    frame.nWidth,
                    frame.nHeight,
                    96, 96, // Standard DPI
                    pf,
                    null,    // Palette
                    imageData,
                    stride);
                
                bitmap.Freeze();
                return bitmap;
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
                // ... more SDK general errors ...
                unchecked((int)0x80000100) => "GenICam：通用错误 (MV_VOLM_E_GC_GENERIC)",
                // ... more GenICam errors ...
                unchecked((int)0x80000204) => "GigE：设备忙或网络断开 (MV_VOLM_E_BUSY)",
                // ... more GigE errors ...
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
    }
} 
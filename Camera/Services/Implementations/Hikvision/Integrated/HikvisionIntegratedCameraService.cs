using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;
using Camera.Interface;
using Camera.Models;
using Common.Models.Package;
using MvLogisticsSDKNet; 
using Serilog;
using System.IO; 

namespace Camera.Services.Implementations.Hikvision.Integrated
{
    public class HikvisionIntegratedCameraService : ICameraService 
    {
        private readonly MvLogistics _mvLogistics;
        private bool _isConnected;
        private readonly Subject<PackageInfo> _packageSubject = new();
        private readonly Subject<(BitmapSource Image, string CameraId)> _imageStreamWithIdSubject = new();

        private readonly MvLogistics.cbOutputdelegate _packageCallBackDelegate;
        private readonly MvLogistics.cbExceptiondelegate _exceptionCallBackDelegate;
        private readonly MvLogistics.cbNoReaddelegate _noReadImageCallBackDelegate;
        private readonly MvLogistics.cbTriggerOutputdelegate _triggerInfoCallBackDelegate;

        private const string ConfigFilePath = "MvLogisticsSDK.xml";

        public HikvisionIntegratedCameraService()
        {
            _mvLogistics = new MvLogistics();
            _packageCallBackDelegate = PackageCallBackFunc;
            _exceptionCallBackDelegate = ExceptionCallBackFunc;
            _noReadImageCallBackDelegate = NoReadImageCallBackFunc;
            _triggerInfoCallBackDelegate = TriggerInfoCallBackFunc;
            Log.Information("[HikvisionIntegratedCameraService] 实例已创建.");
        }

        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                if (_isConnected == value) return;
                _isConnected = value;
                ConnectionChanged?.Invoke(null, _isConnected);
            }
        }

        public IObservable<PackageInfo> PackageStream => _packageSubject.AsObservable();

        public IObservable<(BitmapSource Image, string CameraId)> ImageStreamWithId =>
            _imageStreamWithIdSubject.AsObservable();

        public event Action<string?, bool>? ConnectionChanged;

        public bool Start()
        {
            Log.Information("[HikvisionIntegratedCameraService] 开始启动服务...");
            var nRet = _mvLogistics.MV_LGS_CreateHandle_NET();
            if (nRet != MvLogistics.MV_LGS_OK)
            {
                Log.Error("[HikvisionIntegratedCameraService] 创建SDK句柄失败，错误码: {ErrorCode:X8}", nRet);
                return false;
            }
            Log.Information("[HikvisionIntegratedCameraService] 创建SDK句柄成功。");

            nRet = _mvLogistics.MV_LGS_LoadDevCfg_NET(ConfigFilePath);
            if (nRet != MvLogistics.MV_LGS_OK)
            {
                Log.Error("[HikvisionIntegratedCameraService] 加载SDK配置文件 '{ConfigFile}' 失败，错误码: {ErrorCode:X8}", ConfigFilePath, nRet);
                _mvLogistics.MV_LGS_DestroyHandle_NET();
                return false;
            }
            Log.Information("[HikvisionIntegratedCameraService] 加载SDK配置文件 '{ConfigFile}' 成功。", ConfigFilePath);

            nRet = _mvLogistics.MV_LGS_RegisterPackageCB_NET(_packageCallBackDelegate, IntPtr.Zero);
            if (nRet != MvLogistics.MV_LGS_OK)            {
                Log.Error("[HikvisionIntegratedCameraService] 注册包裹信息回调失败，错误码: {ErrorCode:X8}", nRet);
                _mvLogistics.MV_LGS_DestroyHandle_NET();
                return false;
            }
            Log.Information("[HikvisionIntegratedCameraService] 注册包裹信息回调成功。");

            nRet = _mvLogistics.MV_LGS_RegisterExceptionCB_NET(_exceptionCallBackDelegate, IntPtr.Zero);
            if (nRet != MvLogistics.MV_LGS_OK)            {
                Log.Error("[HikvisionIntegratedCameraService] 注册异常回调失败，错误码: {ErrorCode:X8}", nRet);
                _mvLogistics.MV_LGS_DestroyHandle_NET();
                return false;
            }
            Log.Information("[HikvisionIntegratedCameraService] 注册异常回调成功。");

            nRet = _mvLogistics.MV_LGS_RegisterNoReadImageCB_NET(_noReadImageCallBackDelegate, IntPtr.Zero);
            if (nRet != MvLogistics.MV_LGS_OK)            {
                Log.Error("[HikvisionIntegratedCameraService] 注册NoRead图像回调失败，错误码: {ErrorCode:X8}", nRet);
                _mvLogistics.MV_LGS_DestroyHandle_NET();
                return false;
            }
            Log.Information("[HikvisionIntegratedCameraService] 注册NoRead图像回调成功。");

            nRet = _mvLogistics.MV_LGS_RegisterTriggerInfoCB_NET(_triggerInfoCallBackDelegate, IntPtr.Zero);
            if (nRet != MvLogistics.MV_LGS_OK)            {
                Log.Error("[HikvisionIntegratedCameraService] 注册触发信息回调失败，错误码: {ErrorCode:X8}", nRet);
                _mvLogistics.MV_LGS_DestroyHandle_NET();
                return false;
            }
            Log.Information("[HikvisionIntegratedCameraService] 注册触发信息回调成功。");

            nRet = _mvLogistics.MV_LGS_Start_NET();
            if (nRet != MvLogistics.MV_LGS_OK)            {
                Log.Error("[HikvisionIntegratedCameraService] 启动SDK取流失败，错误码: {ErrorCode:X8}", nRet);
                _mvLogistics.MV_LGS_DestroyHandle_NET();
                return false;
            }
            Log.Information("[HikvisionIntegratedCameraService] 启动SDK取流成功。");

            IsConnected = true;
            Log.Information("[HikvisionIntegratedCameraService] 服务启动成功。");
            return true;
        }

        public bool Stop()
        {
            Log.Information("[HikvisionIntegratedCameraService] 开始停止服务...");
            if (!IsConnected)            {
                Log.Information("[HikvisionIntegratedCameraService] 当前未标记为已连接，无需执行SDK停止操作。");
                return true;
            }

            int nRetStop = _mvLogistics.MV_LGS_Stop_NET();
            if (nRetStop != MvLogistics.MV_LGS_OK)            {
                Log.Error("[HikvisionIntegratedCameraService] 停止SDK取流失败，错误码: {ErrorCode:X8}", nRetStop);
            }
            else
            {
                Log.Information("[HikvisionIntegratedCameraService] 停止SDK取流成功。");
            }

            bool destroySuccess = false;
            int nRetDestroy = _mvLogistics.MV_LGS_DestroyHandle_NET();
            if (nRetDestroy != MvLogistics.MV_LGS_OK)            {
                Log.Error("[HikvisionIntegratedCameraService] 销毁SDK句柄失败，错误码: {ErrorCode:X8}", nRetDestroy);
            }
            else
            {
                Log.Information("[HikvisionIntegratedCameraService] 销毁SDK句柄成功。");
                destroySuccess = true;
            }
            IsConnected = false;
            Log.Information("[HikvisionIntegratedCameraService] 服务已处理停止请求。");
            return destroySuccess;
        }

        private void PackageCallBackFunc(IntPtr pstPkgInfo, IntPtr pUser)
        {
            if (pstPkgInfo == IntPtr.Zero)            {
                Log.Warning("[HikvisionIntegratedCameraService] 包裹信息回调接收到空指针。");
                return;
            }
            try
            {
                object? structure = Marshal.PtrToStructure(pstPkgInfo, typeof(MvLogistics.MVLGS_PACKAGE_INFOEx));
                if (structure == null)
                {
                    Log.Warning("[HikvisionIntegratedCameraService] Marshal.PtrToStructure 返回 null，无法处理包裹信息。");
                    return;
                }
                var stPackageInfoEx = (MvLogistics.MVLGS_PACKAGE_INFOEx)structure;
                Log.Information("[HikvisionIntegratedCameraService] 收到包裹回调，触发序号: {TriggerIndex}", stPackageInfoEx.stCodeList.Length > 0 ? stPackageInfoEx.stCodeList[0].stImage.nTriggerIndex : -1);

                var packageInfo = PackageInfo.Create();
                // 使用SDK提供的 llTriggerStartTimeStamp 设置触发时间
                if (stPackageInfoEx.llTriggerStartTimeStamp > 0) // 确保时间戳有效
                {
                    try
                    {
                        var triggerTime = DateTimeOffset.FromUnixTimeMilliseconds(stPackageInfoEx.llTriggerStartTimeStamp).DateTime;
                        packageInfo.TriggerTimestamp = triggerTime;
                        Log.Debug("[HikvisionIntegratedCameraService] 包裹触发时间已设置为SDK提供的值: {TriggerTime}", triggerTime);
                    }
                    catch (ArgumentOutOfRangeException ex)
                    {
                        Log.Error(ex, "[HikvisionIntegratedCameraService] 无效的 llTriggerStartTimeStamp ({TimestampValue})，无法转换为DateTimeOffset。将使用当前时间作为备用。", stPackageInfoEx.llTriggerStartTimeStamp);
                        packageInfo.TriggerTimestamp = DateTime.Now; // Fallback
                    }
                }
                else
                {
                    Log.Warning("[HikvisionIntegratedCameraService] SDK提供的 llTriggerStartTimeStamp ({TimestampValue}) 无效或为0。将使用当前时间作为包裹触发时间。", stPackageInfoEx.llTriggerStartTimeStamp);
                    packageInfo.TriggerTimestamp = DateTime.Now; // Fallback if timestamp is not positive
                }

                if (stPackageInfoEx.bCodeEnable)
                {
                    var barcodes = new List<string>();
                    bool mainImageSet = false; 
                    foreach (var codeListItem in stPackageInfoEx.stCodeList)                    {
                        for (int j = 0; j < codeListItem.nCodeNum; ++j)
                        {
                            int nLength = Array.FindIndex(codeListItem.stCodeInfo[j].strCode, b => b == 0);
                            if (nLength == -1) nLength = codeListItem.stCodeInfo[j].strCode.Length;
                            string strBarCode = Encoding.UTF8.GetString(codeListItem.stCodeInfo[j].strCode, 0, nLength);
                            barcodes.Add(strBarCode);
                            Log.Debug("[HikvisionIntegratedCameraService] 条码: {Barcode}, 类型: {BarcodeType}, 角度: {Angle}", strBarCode, codeListItem.stCodeInfo[j].enBarType, codeListItem.stCodeInfo[j].nAngle);
                        }
                        if (!mainImageSet && codeListItem.stImage.nImageLen > 0 && codeListItem.stImage.pImageBuf != IntPtr.Zero)
                        {
                            Log.Debug("[HikvisionIntegratedCameraService] PackageCallBackFunc: 尝试处理codeListItem中的图像: Type={ImageType}, Len={ImageLen}, W={Width}, H={Height}", 
                                      codeListItem.stImage.enImageType, codeListItem.stImage.nImageLen, codeListItem.stImage.nWidth, codeListItem.stImage.nHeight);
                            BitmapSource? imageFromCodeList = ConvertHikImageToBitmapSource(
                                codeListItem.stImage.pImageBuf,
                                codeListItem.stImage.nWidth,
                                codeListItem.stImage.nHeight,
                                codeListItem.stImage.enImageType,
                                codeListItem.stImage.nImageLen);
                            if (imageFromCodeList != null)
                            {
                                packageInfo.SetImage(imageFromCodeList, null); 
                                Log.Information("[HikvisionIntegratedCameraService] PackageCallBackFunc: 已将codeListItem中的图像设置到PackageInfo.Image");
                                mainImageSet = true; 
                            }
                            else
                            {
                                Log.Warning("[HikvisionIntegratedCameraService] PackageCallBackFunc: 未能从codeListItem转换图像: Type={ImageType}", codeListItem.stImage.enImageType);
                            }
                        }
                    }
                    packageInfo.SetBarcode(string.Join(";", barcodes));
                }

                if (stPackageInfoEx.bVolumeEnable)                {
                    packageInfo.SetDimensions(
                        stPackageInfoEx.stVolumeInfo.fLength,
                        stPackageInfoEx.stVolumeInfo.fWidth,
                        stPackageInfoEx.stVolumeInfo.fHeight);
                    packageInfo.Volume = stPackageInfoEx.stVolumeInfo.fVolume;
                    Log.Debug("[HikvisionIntegratedCameraService] 体积: L={Length}, W={Width}, H={Height}, V={Volume}", packageInfo.Length, packageInfo.Width, packageInfo.Height, packageInfo.Volume);
                }

                if (stPackageInfoEx.bWeightEnable)                {
                    packageInfo.Weight = stPackageInfoEx.fWeight;
                    Log.Debug("[HikvisionIntegratedCameraService] 重量: {Weight}", packageInfo.Weight);
                }
                packageInfo.SetStatus(PackageStatus.Created, "包裹已创建/检测到");
                _packageSubject.OnNext(packageInfo);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[HikvisionIntegratedCameraService] 处理包裹回调时发生异常。");
            }
        }

        private void ExceptionCallBackFunc(ref MvLogistics.MVLGS_EXCEPTION_INFO pstEcptInfo, IntPtr pUser)
        {
            Log.Error("[HikvisionIntegratedCameraService] SDK异常: ID={ExceptionId}, 设备类型={DeviceType}, SN={SerialNumber}, 描述='{Description}'", pstEcptInfo.nExceptionID, pstEcptInfo.enCamType, pstEcptInfo.strCamSerialNum, pstEcptInfo.strExceptionDes);
        }

        private void NoReadImageCallBackFunc(IntPtr noReadInfoPtr, IntPtr pUser)
        {
            if (noReadInfoPtr == IntPtr.Zero)            {
                Log.Warning("[HikvisionIntegratedCameraService] NoRead图像回调接收到空指针。");
                return;
            }
            try
            {
                object? structure = Marshal.PtrToStructure(noReadInfoPtr, typeof(MvLogistics.MVLGS_IMAGE_OUTPUT_INFO));
                if (structure == null)
                {
                    Log.Warning("[HikvisionIntegratedCameraService] Marshal.PtrToStructure 返回 null，无法处理NoRead图像信息。");
                    return;
                }
                var stNoReadInfo = (MvLogistics.MVLGS_IMAGE_OUTPUT_INFO)structure;
                Log.Information("[HikvisionIntegratedCameraService] 收到NoRead图像回调: SN={SerialNumber}, Trigger={TriggerIndex}, W={Width}, H={Height}, Type={ImageType}, Len={ImageLen}", 
                                stNoReadInfo.strSerialNumber, 
                                stNoReadInfo.stImage.nTriggerIndex, 
                                stNoReadInfo.stImage.nWidth, 
                                stNoReadInfo.stImage.nHeight, 
                                stNoReadInfo.stImage.enImageType, 
                                stNoReadInfo.stImage.nImageLen);

                BitmapSource? bitmapSource = null;
                if (stNoReadInfo.stImage.nImageLen > 0 && stNoReadInfo.stImage.pImageBuf != IntPtr.Zero)
                {
                    bitmapSource = ConvertHikImageToBitmapSource(
                        stNoReadInfo.stImage.pImageBuf,
                        stNoReadInfo.stImage.nWidth,
                        stNoReadInfo.stImage.nHeight,
                        stNoReadInfo.stImage.enImageType,
                        stNoReadInfo.stImage.nImageLen);
                
                    if (bitmapSource != null)
                    {
                        // 仍然通过 _imageStreamWithIdSubject 推送原始图像，以防其他地方需要
                        _imageStreamWithIdSubject.OnNext((bitmapSource, stNoReadInfo.strSerialNumber ?? "UnknownHikCamera_NoRead"));
                        Log.Debug("[HikvisionIntegratedCameraService] NoRead图像已处理并推送到 _imageStreamWithIdSubject: CameraId={CameraId}", stNoReadInfo.strSerialNumber);
                    }
                }

                // 为 NoRead 事件创建一个 PackageInfo 对象并通过 _packageSubject 推送
                var noReadPackageInfo = PackageInfo.Create();
                noReadPackageInfo.SetBarcode("NOREAD");
                noReadPackageInfo.SetStatus(PackageStatus.NoRead, "NoRead event from camera");
                noReadPackageInfo.TriggerTimestamp = DateTime.Now; // 或者尝试从 stNoReadInfo 获取更精确的时间
                if (bitmapSource != null)
                {
                    noReadPackageInfo.SetImage(bitmapSource, null); // 将转换后的图像赋给 PackageInfo
                    Log.Information("[HikvisionIntegratedCameraService] NoRead事件的图像已设置到PackageInfo中。");
                }
                else
                {
                    Log.Warning("[HikvisionIntegratedCameraService] NoRead事件未获取到有效图像，PackageInfo中将不包含图像。");
                }
                // 设置相机ID或其他可识别信息（如果需要且可用）
                // noReadPackageInfo.SetExtendedProperty("CameraId", stNoReadInfo.strSerialNumber ?? "UnknownHikCamera_NoRead");
                
                _packageSubject.OnNext(noReadPackageInfo);
                Log.Information("[HikvisionIntegratedCameraService] NoRead事件已作为PackageInfo推送到_packageSubject。 Barcode: {Barcode}", noReadPackageInfo.Barcode);

            }
            catch (Exception ex)
            {
                Log.Error(ex, "[HikvisionIntegratedCameraService] 处理NoRead图像回调时发生异常。");
            }
        }

        private static void TriggerInfoCallBackFunc(ref MvLogistics.MVLGS_TRIGGER_INFO pstTriggerInfo, IntPtr pUser)
        {
            string triggerStatus = pstTriggerInfo.nTriggerFlag == MvLogistics.MV_LGS_BEGIN_TRIGGER ? "开始触发" : "停止触发";
            Log.Information("[HikvisionIntegratedCameraService] 触发信息回调: 序号={TriggerIndex}, 状态={TriggerStatus}", pstTriggerInfo.nTriggerIndex, triggerStatus);
        }

        private static BitmapSource? ConvertHikImageToBitmapSource(IntPtr imageDataPtr, ushort width, ushort height, MvLogistics.MVLGS_IMAGE_TYPE imageType, uint imageLen)
        {
            if (imageDataPtr == IntPtr.Zero || width == 0 || height == 0 || imageLen == 0)
            {
                Log.Warning("[HikvisionIntegratedCameraService] 无效的图像参数 (ptr,w,h,len): {Ptr}, {W}, {H}, {Len}", imageDataPtr, width, height, imageLen);
                return null;
            }
        
            try
            {
                BitmapSource? bitmap = null;
                byte[] buffer;

                switch (imageType)
                {
                    case MvLogistics.MVLGS_IMAGE_TYPE.MVLGS_IMAGE_MONO8:
                        int strideMono = width;
                        buffer = new byte[imageLen]; 
                        Marshal.Copy(imageDataPtr, buffer, 0, (int)imageLen);
                        bitmap = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Gray8, null, buffer, strideMono);
                        break;

                    case MvLogistics.MVLGS_IMAGE_TYPE.MVLGS_IMAGE_BGR24:
                        int strideBgr = width * 3;
                        buffer = new byte[imageLen]; 
                        Marshal.Copy(imageDataPtr, buffer, 0, (int)imageLen);
                        bitmap = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null, buffer, strideBgr);
                        break;

                    case MvLogistics.MVLGS_IMAGE_TYPE.MVLGS_IMAGE_RGB24:
                        int strideRgb = width * 3;
                        buffer = new byte[imageLen]; 
                        Marshal.Copy(imageDataPtr, buffer, 0, (int)imageLen);
                        bitmap = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Rgb24, null, buffer, strideRgb);
                        break;

                    case MvLogistics.MVLGS_IMAGE_TYPE.MVLGS_IMAGE_JPEG:
                        buffer = new byte[imageLen];
                        Marshal.Copy(imageDataPtr, buffer, 0, (int)imageLen);
                        using (var stream = new MemoryStream(buffer))
                        {
                            var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                            if (decoder.Frames.Count > 0) bitmap = decoder.Frames[0];
                        }
                        break;

                    case MvLogistics.MVLGS_IMAGE_TYPE.MVLGS_IMAGE_BMP:
                        buffer = new byte[imageLen];
                        Marshal.Copy(imageDataPtr, buffer, 0, (int)imageLen);
                        using (var stream = new MemoryStream(buffer))
                        {
                            var decoder = new BmpBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                            if (decoder.Frames.Count > 0) bitmap = decoder.Frames[0];
                        }
                        break;
                    
                    case MvLogistics.MVLGS_IMAGE_TYPE.MVLGS_IMAGE_Undefined:
                    default:
                        Log.Warning("[HikvisionIntegratedCameraService] 不支持或未定义的海康图像类型: {ImageType}", imageType);
                        return null;
                }

                if (bitmap != null)
                {
                    bitmap.Freeze(); 
                    return bitmap;
                }
                Log.Warning("[HikvisionIntegratedCameraService] BitmapSource创建失败，图像类型: {ImageType}", imageType);
                return null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[HikvisionIntegratedCameraService] 将海康图像数据(类型:{ImageType})转换为BitmapSource时发生异常。 W:{W}, H:{H}, Len:{Len}", imageType, width, height, imageLen);
                return null;
            }
        }

        public void Dispose()
        {
            Log.Information("[HikvisionIntegratedCameraService] 正在释放资源...");
            Stop();
            _packageSubject.Dispose();
            _imageStreamWithIdSubject.Dispose();
            Log.Information("[HikvisionIntegratedCameraService] 资源已释放。");
            GC.SuppressFinalize(this);
        }
    }
} 
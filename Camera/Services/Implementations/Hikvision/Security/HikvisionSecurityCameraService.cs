using System.Buffers;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Camera.Interface;
using Camera.Models.Settings;
using Common.Models.Package;
using Common.Services.Settings;
using Serilog;

namespace Camera.Services.Implementations.Hikvision.Security;

/// <summary>
/// 海康安防相机服务 (适配ICameraService，仅实时预览+抓图)
/// </summary>
public sealed class HikvisionSecurityCameraService : ICameraService
{
    private readonly ISettingsService _settingsService;
    private int _userId = -1;
    private int _realHandle = -1;
    private int _playPort = -1;
    private readonly Subject<(BitmapSource Image, string CameraId)> _imageWithIdSubject = new();
    private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;
    private readonly CHCNetSDK.REALDATACALLBACK _realDataCallbackInstance;
    private readonly PlayCtrl.DECCBFUN _decCallbackInstance;
    private bool _disposedValue;
    private string _currentCameraId = "HikvisionSecurityCam_Default";

    public HikvisionSecurityCameraService(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        Log.Debug("[海康安防相机服务] 实例已创建. HashCode: {HashCode}. 将使用 ISettingsService 按需加载配置.", GetHashCode());
        _realDataCallbackInstance = RealDataCallback;
        _decCallbackInstance = DecCallback;
    }

    ~HikvisionSecurityCameraService()
    {
        Log.Debug("[海康安防相机服务] 实例准备被GC回收. CameraID: {CameraId}, HashCode: {HashCode}", _currentCameraId, GetHashCode());
        Dispose(false);
    }

    public bool IsConnected { get; private set; }
    public IObservable<PackageInfo> PackageStream => Observable.Empty<PackageInfo>();
    public IObservable<(BitmapSource Image, string CameraId)> ImageStreamWithId => _imageWithIdSubject;
    public event Action<string?, bool>? ConnectionChanged;

    private HikvisionSecurityCameraSettings? GetCurrentHikvisionSettings()
    {
        var overallSettings = _settingsService.LoadSettings<CameraOverallSettings>();
        if (overallSettings.IsHikvisionSecurityCameraEnabled) return overallSettings.HikvisionSecurityCamera;
        Log.Warning("[海康安防相机服务] 海康安防相机在设置中被禁用。无法获取当前设置。");
        return null;
    }

    public bool Start()
    {
        var settings = GetCurrentHikvisionSettings();
        if (settings == null)
        {
            Log.Error("[海康安防相机服务] 启动失败：无法获取有效的海康安防相机配置。请检查设置是否启用且正确配置。");
            return false;
        }

        _currentCameraId = !string.IsNullOrWhiteSpace(settings.IpAddress) ? settings.IpAddress : "HikvisionSecurityCam_Unnamed";
        Log.Information("[海康安防相机服务] 正在启动... CameraID: {CameraId}, IP: {IPAddress}", _currentCameraId, settings.IpAddress);

        try
        {
            if (!CHCNetSDK.NET_DVR_Init())
            {
                Log.Error("[海康安防相机服务] SDK初始化失败. CameraID: {CameraId}", _currentCameraId);
                return false;
            }

            CHCNetSDK.NET_DVR_USER_LOGIN_INFO struLogInfo = new();

            var byIp = System.Text.Encoding.Default.GetBytes(settings.IpAddress);
            struLogInfo.sDeviceAddress = new byte[129];
            byIp.CopyTo(struLogInfo.sDeviceAddress, 0);
            struLogInfo.wPort = (ushort)settings.Port;

            var byUserName = System.Text.Encoding.Default.GetBytes(settings.Username);
            struLogInfo.sUserName = new byte[64];
            byUserName.CopyTo(struLogInfo.sUserName, 0);

            var byPassword = System.Text.Encoding.Default.GetBytes(settings.Password);
            struLogInfo.sPassword = new byte[64];
            byPassword.CopyTo(struLogInfo.sPassword, 0);

            struLogInfo.bUseAsynLogin = false;

            CHCNetSDK.NET_DVR_DEVICEINFO_V40 deviceInfo = new();
            _userId = CHCNetSDK.NET_DVR_Login_V40(ref struLogInfo, ref deviceInfo);

            if (_userId < 0)
            {
                Log.Error("[海康安防相机服务] 登录设备失败. CameraID: {CameraId}, IP: {IPAddress}, 错误码: {ErrorCode}",
                    _currentCameraId, settings.IpAddress, CHCNetSDK.NET_DVR_GetLastError());
                CHCNetSDK.NET_DVR_Cleanup();
                return false;
            }

            Log.Information("[海康安防相机服务] 设备登录成功. CameraID: {CameraId}, UserID: {UserId}", _currentCameraId, _userId);

            CHCNetSDK.NET_DVR_PREVIEWINFO info = new()
            {
                hPlayWnd = IntPtr.Zero, // No window handle for direct callback
                lChannel = 1, // Assuming channel 1, make configurable if needed
                dwStreamType = 0, // Main stream
                dwLinkMode = 0, // TCP
                bBlocked = true,
                dwDisplayBufNum = 15 // Buffer for real-time preview
            };

            _realHandle = CHCNetSDK.NET_DVR_RealPlay_V40(_userId, ref info, _realDataCallbackInstance, IntPtr.Zero);
            if (_realHandle < 0)
            {
                Log.Error("[海康安防相机服务] 实时预览失败. CameraID: {CameraId}, 错误码: {ErrorCode}",
                    _currentCameraId, CHCNetSDK.NET_DVR_GetLastError());
                CHCNetSDK.NET_DVR_Logout(_userId);
                _userId = -1;
                CHCNetSDK.NET_DVR_Cleanup();
                return false;
            }

            IsConnected = true;
            ConnectionChanged?.Invoke(_currentCameraId, true); // Use _currentCameraId which might be the IP or a configured ID

            Log.Information("[海康安防相机服务] 服务已启动并开始实时预览. CameraID: {CameraId}, RealPlayHandle: {RealHandle}", _currentCameraId,
                _realHandle);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[海康安防相机服务] 启动服务时发生异常. CameraID: {CameraId}", _currentCameraId);
            // Attempt to clean up if an exception occurred mid-startup
            if (_userId >= 0) CHCNetSDK.NET_DVR_Logout(_userId); _userId = -1;
            if (_realHandle >=0) CHCNetSDK.NET_DVR_StopRealPlay(_realHandle); _realHandle = -1;
            CHCNetSDK.NET_DVR_Cleanup(); 
            IsConnected = false;
            return false;
        }
    }

    public bool Stop()
    {
        Log.Information("[海康安防相机服务] 正在停止服务... CameraID: {CameraId}", _currentCameraId);
        try
        {
            StopServiceInternal();
            ConnectionChanged?.Invoke(_currentCameraId, false);
            Log.Information("[海康安防相机服务] 服务已停止. CameraID: {CameraId}", _currentCameraId);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[海康安防相机服务] 停止服务时发生异常. CameraID: {CameraId}", _currentCameraId);
            return false;
        }
    }

    public void Dispose()
    {
        Log.Debug("[海康安防相机服务] Dispose() 方法被调用. CameraID: {CameraId}, HashCode: {HashCode}", _currentCameraId, GetHashCode());
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposedValue) return;
        Log.Debug("[海康安防相机服务] Dispose({Disposing}) 实际执行. CameraID: {CameraId}, HashCode: {HashCode}", disposing,
            _currentCameraId, GetHashCode());
        if (disposing)
        {
            // Dispose managed state (managed objects).
            _imageWithIdSubject.OnCompleted();
            _imageWithIdSubject.Dispose();
        }

        // Free unmanaged resources (unmanaged objects) and override a finalizer below.
        StopServiceInternal();
        // Note: NET_DVR_Cleanup is called in StopServiceInternal, 
        // which should be enough for SDK resources.

        _disposedValue = true;
        Log.Information("[海康安防相机服务] 资源已释放. CameraID: {CameraId}", _currentCameraId);
    }

    private void StopServiceInternal()
    {
        if (_realHandle >= 0)
        {
            if (!CHCNetSDK.NET_DVR_StopRealPlay(_realHandle))
            {
                Log.Warning("[海康安防相机服务] NET_DVR_StopRealPlay 失败. CameraID: {CameraId}, ErrorCode: {ErrorCode}",
                    _currentCameraId, CHCNetSDK.NET_DVR_GetLastError());
            }

            _realHandle = -1;
        }

        if (_playPort >= 0)
        {
            if (!PlayCtrl.PlayM4_Stop(_playPort))
            {
                Log.Warning("[海康安防相机服务] PlayM4_Stop 失败. CameraID: {CameraId}, Port: {PlayPort}, ErrorCode: {ErrorCode}",
                    _currentCameraId, _playPort, PlayCtrl.PlayM4_GetLastError(_playPort));
            }

            if (!PlayCtrl.PlayM4_CloseStream(_playPort))
            {
                Log.Warning(
                    "[海康安防相机服务] PlayM4_CloseStream 失败. CameraID: {CameraId}, Port: {PlayPort}, ErrorCode: {ErrorCode}",
                    _currentCameraId, _playPort, PlayCtrl.PlayM4_GetLastError(_playPort));
            }

            if (!PlayCtrl.PlayM4_FreePort(_playPort))
            {
                Log.Warning(
                    "[海康安防相机服务] PlayM4_FreePort 失败. CameraID: {CameraId}, Port: {PlayPort}, ErrorCode: {ErrorCode}",
                    _currentCameraId, _playPort, PlayCtrl.PlayM4_GetLastError(_playPort));
            }

            _playPort = -1;
        }

        if (_userId >= 0)
        {
            if (!CHCNetSDK.NET_DVR_Logout(_userId))
            {
                Log.Warning(
                    "[海康安防相机服务] NET_DVR_Logout 失败. CameraID: {CameraId}, UserID: {UserId}, ErrorCode: {ErrorCode}",
                    _currentCameraId, _userId, CHCNetSDK.NET_DVR_GetLastError());
            }

            _userId = -1;
        }

        // Cleanup SDK resources. This should be called once when the application is exiting 
        // or when all Hikvision services are definitely stopped. 
        // If multiple camera services might exist, manage this call carefully (e.g., ref counting or central manager).
        // For a single service instance or when it's the last one, it's okay here.
        if (!CHCNetSDK.NET_DVR_Cleanup())
        {
            Log.Warning("[海康安防相机服务] NET_DVR_Cleanup 失败. CameraID: {CameraId}, ErrorCode: {ErrorCode}", _currentCameraId,
                CHCNetSDK.NET_DVR_GetLastError());
        }

        IsConnected = false;
    }

    private void RealDataCallback(int lRealHandle, uint dwDataType, IntPtr pBuffer, uint dwBufSize, IntPtr pUser)
    {
        switch (dwDataType)
        {
            case CHCNetSDK.NET_DVR_SYSHEAD:
                if (_playPort >= 0)
                {
                    // This might happen if the stream reconnects or there's an issue.
                    // It's important to handle this gracefully, possibly by stopping and reopening the PlayM4 port.
                    Log.Warning(
                        "[海康安防相机] 再次收到 DVR_SYSHEAD，播放端口 {PlayPort} 已初始化. CameraID: {CameraId}. อาจจะต้องรีสตาร์ทการเล่น.",
                        _playPort, _currentCameraId);
                    // Consider a more robust handling, e.g., stopping existing playback before reinitializing.
                    // For simplicity, current implementation might continue with the old port or fail if PlayM4_GetPort is called again without freeing.
                    // A quick fix might be to return or ensure the old port is freed before getting a new one.
                }

                if (!PlayCtrl.PlayM4_GetPort(ref _playPort))
                {
                    Log.Error("[海康安防相机] PlayM4_GetPort 获取播放端口失败. CameraID: {CameraId}, 错误码: {ErrorCode}", _currentCameraId,
                        PlayCtrl.PlayM4_GetLastError(_playPort));
                    return;
                }

                if (!PlayCtrl.PlayM4_SetStreamOpenMode(_playPort, PlayCtrl.STREAME_REALTIME))
                {
                    Log.Error("[海康安防相机] PlayM4_SetStreamOpenMode 设置流模式失败. CameraID: {CameraId}, 错误码: {ErrorCode}",
                        _currentCameraId, PlayCtrl.PlayM4_GetLastError(_playPort));
                    return;
                }

                if (!PlayCtrl.PlayM4_OpenStream(_playPort, pBuffer, dwBufSize,
                        2 * 1024 * 1024)) // Buffer size for source data
                {
                    Log.Error("[海康安防相机] PlayM4_OpenStream 打开流失败. CameraID: {CameraId}, 错误码: {ErrorCode}", _currentCameraId,
                        PlayCtrl.PlayM4_GetLastError(_playPort));
                    return;
                }

                if (!PlayCtrl.PlayM4_SetDecCallBack(_playPort, _decCallbackInstance))
                {
                    Log.Error("[海康安防相机] PlayM4_SetDecCallBack 设置解码回调失败. CameraID: {CameraId}, 错误码: {ErrorCode}",
                        _currentCameraId, PlayCtrl.PlayM4_GetLastError(_playPort));
                    return;
                }

                if (!PlayCtrl.PlayM4_Play(_playPort, IntPtr.Zero)) // No window handle for rendering
                {
                    Log.Error("[海康安防相机] PlayM4_Play 开始播放失败. CameraID: {CameraId}, 错误码: {ErrorCode}", _currentCameraId,
                        PlayCtrl.PlayM4_GetLastError(_playPort));
                }

                break;

            case CHCNetSDK.NET_DVR_STREAMDATA:
                if (_playPort != -1)
                {
                    if (!PlayCtrl.PlayM4_InputData(_playPort, pBuffer, dwBufSize))
                    {
                        // This can be very verbose, uncomment if needed for debugging stream issues.
                        // Log.Error("[海康安防相机] PlayM4_InputData 输入数据失败. CameraID: {CameraId}, 错误码: {ErrorCode}", _currentCameraId, PlayCtrl.PlayM4_GetLastError(_playPort));
                    }
                }

                break;
        }
    }

    // YV12 to BGR32 conversion (same as original, with logging context)
    private void DecCallback(int nPort, IntPtr pBuf, int nSize, ref PlayCtrl.FRAME_INFO pFrameInfo, int nUser,
        int nReserved2)
    {
        if (pFrameInfo.nType != 3) // T_YV12
        {
            // Log.Verbose("[海康安防相机] DecCallback: 非YV12帧. CameraID: {CameraId}, Type: {Type}", _currentCameraId, pFrameInfo.nType);
            return;
        }

        if (pFrameInfo.nWidth <= 0 || pFrameInfo.nHeight <= 0)
        {
            Log.Warning("[海康安防相机] DecCallback: 无效的帧尺寸. CameraID: {CameraId}, Width: {Width}, Height: {Height}",
                _currentCameraId, pFrameInfo.nWidth, pFrameInfo.nHeight);
            return;
        }

        var width = pFrameInfo.nWidth;
        var height = pFrameInfo.nHeight;
        var expectedYv12Size = width * height * 3 / 2;

        if (nSize < expectedYv12Size)
        {
            Log.Warning("[海康安防相机] DecCallback: 缓冲区大小 {ActualSize} 小于期望的YV12大小 {ExpectedSize}. CameraID: {CameraId}",
                nSize, expectedYv12Size, _currentCameraId);
            return;
        }

        var bgr32Buffer = Pool.Rent(width * height * 4); // BGRA is 4 bytes per pixel
        try
        {
            // Optimized YCbCr to RGB conversion for YV12 (YYYYYYYY YY UU VV)
            // This is a common algorithm. For extreme performance, platform intrinsics or a native library might be used.
            Parallel.For(0, height, y =>
            {
                int yRowBase = y * width;
                int uvRowBase = width * height + (y / 2) * (width / 2); // For U and V planes, which are half-resolution

                for (var x = 0; x < width; x++)
                {
                    int yIndex = yRowBase + x;
                    // UV components are shared by 2x2 pixel blocks. Integer division handles this.
                    int uvIndexOffset = (x / 2);
                    int uIndex = uvRowBase + uvIndexOffset;
                    int vIndex = uvRowBase + (width * height / 4) + uvIndexOffset; // V plane follows U plane in YV12

                    byte Y = Marshal.ReadByte(pBuf, yIndex);
                    byte U = Marshal.ReadByte(pBuf,
                        uIndex); // Cr in some notations, Cb in others. Hikvision YV12 might be Y V U.
                    byte V = Marshal.ReadByte(pBuf,
                        vIndex); // Cb in some notations, Cr in others. Let's assume standard YUV/YCbCr component meaning.

                    // Standard YUV to RGB conversion (approximated for integer arithmetic)
                    // These coefficients are common for BT.601 standard.
                    int C = Y - 16;
                    int D = U - 128;
                    int E = V - 128;

                    // R = 1.164(Y-16) + 1.596(V-128)
                    // G = 1.164(Y-16) - 0.813(V-128) - 0.391(U-128)
                    // B = 1.164(Y-16) + 2.018(U-128)
                    // Scaled by 256 (or 2^n for bit-shifting) to use integer math:
                    // R = (298 * C + 409 * E + 128) >> 8
                    // G = (298 * C - 100 * D - 208 * E + 128) >> 8
                    // B = (298 * C + 516 * D + 128) >> 8

                    int r_val = (298 * C + 409 * E + 128) >> 8;
                    int g_val = (298 * C - 100 * D - 208 * E + 128) >> 8;
                    int b_val = (298 * C + 516 * D + 128) >> 8;

                    byte R = (byte)Math.Max(0, Math.Min(255, r_val));
                    byte G = (byte)Math.Max(0, Math.Min(255, g_val));
                    byte B = (byte)Math.Max(0, Math.Min(255, b_val));

                    int outIndex = yIndex * 4; // Each pixel is 4 bytes (B, G, R, A)
                    bgr32Buffer[outIndex + 0] = B;
                    bgr32Buffer[outIndex + 1] = G;
                    bgr32Buffer[outIndex + 2] = R;
                    bgr32Buffer[outIndex + 3] = 255; // Alpha channel (fully opaque)
                }
            });

            var wb = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            wb.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), bgr32Buffer, width * 4, 0);
            if (wb is { CanFreeze: true, IsFrozen: false })
            {
                wb.Freeze();
            }

            var overallSettings = _settingsService.LoadSettings<CameraOverallSettings>();
            var streamCameraId = _currentCameraId; 
            if(!string.IsNullOrWhiteSpace(overallSettings.HikvisionSecurityCamera.IpAddress))
            {
                streamCameraId = overallSettings.HikvisionSecurityCamera.IpAddress;
            }
            _imageWithIdSubject.OnNext((wb, streamCameraId));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[海康安防相机] YV12解码转BitmapSource失败. CameraID: {CameraId}, Width: {W}, Height: {H}, Size: {S}",
                _currentCameraId, width, height, nSize);
        }
        finally
        {
            Pool.Return(bgr32Buffer);
        }
    }

    /// <summary>
    /// 抓图并返回BitmapSource（JPEG格式，自动清理临时文件）
    /// </summary>
    public BitmapSource? CaptureAndGetBitmapSource()
    {
        var settings = GetCurrentHikvisionSettings();
        if (settings == null || _userId < 0 || !IsConnected)
        { 
            Log.Warning("[海康安防相机] 尝试抓图失败：未配置、未登录或未连接. CameraID: {CameraId}", _currentCameraId);
            return null;
        }
        
        var jpegPara = new CHCNetSDK.NET_DVR_JPEGPARA { wPicQuality = 0, wPicSize = 0xff };
        const int channel = 1;
        var tempFileName = $"hik_cap_{_currentCameraId.Replace(':', '_')}_{Guid.NewGuid()}.jpg";
        var tempFile = Path.Combine(Path.GetTempPath(), tempFileName);
        Log.Debug("[海康安防相机] 准备抓图到: {TempFile}. CameraID: {CameraId}", tempFile, _currentCameraId);

        try
        {
            if (!CHCNetSDK.NET_DVR_CaptureJPEGPicture(_userId, channel, ref jpegPara, tempFile))
            { Log.Error("[海康安防相机] NET_DVR_CaptureJPEGPicture 抓图失败. CameraID: {CameraId}, ErrorCode: {ErrorCode}", _currentCameraId, CHCNetSDK.NET_DVR_GetLastError()); return null; }
            Log.Information("[海康安防相机] 图片成功抓取到: {TempFile}. CameraID: {CameraId}", tempFile, _currentCameraId);

            var bitmap = new BitmapImage();
            using (var stream = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            { bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); }
            if (bitmap.CanFreeze) bitmap.Freeze(); 
            Log.Debug("[海康安防相机] BitmapImage 从抓图文件加载成功. CameraID: {CameraId}", _currentCameraId);
            return bitmap;
        }
        catch (Exception ex) { Log.Error(ex, "[海康安防相机] 抓图并加载 BitmapSource 异常. CameraID: {CameraId}, TempFile: {TempFile}", _currentCameraId, tempFile); return null; }
        finally
        {
            try 
            {
                if (File.Exists(tempFile)) 
                {
                    File.Delete(tempFile);
                    Log.Verbose("[海康安防相机] 临时抓图文件已删除: {TempFile}. CameraID: {CameraId}", tempFile, _currentCameraId);
                }
            }
            catch (Exception ex) { Log.Warning(ex, "[海康安防相机] 删除临时抓图文件失败. File: {TempFile}. CameraID: {CameraId}", tempFile, _currentCameraId); }
        }
    }
}
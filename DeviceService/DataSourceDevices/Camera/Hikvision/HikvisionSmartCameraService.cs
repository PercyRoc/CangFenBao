using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Text;
using Common.Models.Package;
using DeviceService.DataSourceDevices.Camera.Models;
using DeviceService.DataSourceDevices.Camera.Models.Camera;
using MvCodeReaderSDKNet;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DeviceService.DataSourceDevices.Camera.Hikvision;

/// <summary>
///     海康智能相机服务
/// </summary>
public class HikvisionSmartCameraService : ICameraService
{
    private readonly Subject<PackageInfo> _packageSubject = new();
    private readonly Subject<(Image<Rgba32> image, IReadOnlyList<BarcodeLocation> barcodes)> _imageSubject = new();
    private readonly Dictionary<string, CameraInstance> _cameras = new();
    private bool _disposed;
    private CameraSettings _settings = new();
    private CodeRules _codeRules;
    
    // 节流相关字段
    private readonly Dictionary<string, DateTime> _lastPublishTime = new();
    private readonly Dictionary<string, PackageInfo> _pendingPackages = new();
    private readonly object _publishLock = new();
    private const int ThrottleIntervalMs = 100;
    
    // 使用IP地址作为键的相机条码缓存
    private readonly Dictionary<string, (DateTime Time, string Barcode)> _cameraBarcodeCaches = new();
    private readonly object _cacheUpdateLock = new();
    private const int BarcodeValidTimeMs = 500; // 条码有效期500ms

    // 添加触发计数器和时间戳
    private int _currentTriggerCount = 0;
    private DateTime _lastTriggerTime = DateTime.MinValue;
    private const int TriggerIntervalMs = 200; // 触发间隔
    private readonly Dictionary<string, bool> _cameraProcessed = new();
    private readonly object _triggerLock = new();

    static HikvisionSmartCameraService()
    {
        // 注册编码提供程序
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public HikvisionSmartCameraService()
    {
        // 初始化硬编码的条码规则
        _codeRules = new CodeRules
        {
            coderules = new List<CodeRule>
            {
                new()
                {
                    name = "default",
                    regex = "(^.{0,0}$)",
                    userDefine = false,
                    ui = new CodeRuleUI
                    {
                        blength = true,
                        bstartwith = true,
                        bendwith = true,
                        binclude = true,
                        bexclude = true,
                        bother = true,
                        buserdefine = false,
                        minLen = 0,
                        maxLen = 100,
                        startwith = "",
                        endwith = "",
                        include = "",
                        include_start = 0,
                        include_end = 0,
                        exclude = "",
                        exclude_start = 0,
                        exclude_end = 0,
                        other = 2
                    },
                    enable = false
                },
                new()
                {
                    name = "UN",
                    regex = "(?=^[0-9a-zA-Z]+$)(?=^(UN).*)(?=.*(CN)$)(^.{14,100}$)",
                    userDefine = false,
                    ui = new CodeRuleUI
                    {
                        blength = true,
                        bstartwith = true,
                        bendwith = true,
                        binclude = false,
                        bexclude = false,
                        bother = true,
                        buserdefine = false,
                        minLen = 14,
                        maxLen = 100,
                        startwith = "UN",
                        endwith = "CN",
                        include = "",
                        include_start = 0,
                        include_end = 0,
                        exclude = "",
                        exclude_start = 0,
                        exclude_end = 0,
                        other = 2
                    },
                    enable = true
                },
                new()
                {
                    name = "YANDEX",
                    regex = "(?=^[0-9a-zA-Z]+$)(^.{11,100}$)",
                    userDefine = false,
                    ui = new CodeRuleUI
                    {
                        blength = true,
                        bstartwith = true,
                        bendwith = true,
                        binclude = true,
                        bexclude = true,
                        bother = true,
                        buserdefine = false,
                        minLen = 11,
                        maxLen = 100,
                        startwith = "",
                        endwith = "",
                        include = "",
                        include_start = 0,
                        include_end = 0,
                        exclude = "",
                        exclude_start = 0,
                        exclude_end = 0,
                        other = 2
                    },
                    enable = true
                },
                new()
                {
                    name = "YADEX2",
                    regex = "(?=^[0-9a-zA-Z]+$)(^.{13,100}$)",
                    userDefine = false,
                    ui = new CodeRuleUI
                    {
                        blength = true,
                        bstartwith = true,
                        bendwith = true,
                        binclude = true,
                        bexclude = true,
                        bother = true,
                        buserdefine = false,
                        minLen = 13,
                        maxLen = 100,
                        startwith = "",
                        endwith = "",
                        include = "",
                        include_start = 0,
                        include_end = 0,
                        exclude = "",
                        exclude_start = 0,
                        exclude_end = 0,
                        other = 2
                    },
                    enable = true
                },
                new()
                {
                    name = "EQ",
                    regex = "(?=^[0-9a-zA-Z]+$)(?=^(EQ).*)(?=.*(YQ)$)(^.{17,100}$)",
                    userDefine = false,
                    ui = new CodeRuleUI
                    {
                        blength = true,
                        bstartwith = true,
                        bendwith = true,
                        binclude = true,
                        bexclude = true,
                        bother = true,
                        buserdefine = false,
                        minLen = 17,
                        maxLen = 100,
                        startwith = "EQ",
                        endwith = "YQ",
                        include = "",
                        include_start = 0,
                        include_end = 0,
                        exclude = "",
                        exclude_start = 0,
                        exclude_end = 0,
                        other = 2
                    },
                    enable = true
                },
                new()
                {
                    name = "WB18",
                    regex = "(?=^[0-9a-zA-Z]+$)(?=^(WB).*)(^.{18,100}$)",
                    userDefine = false,
                    ui = new CodeRuleUI
                    {
                        blength = true,
                        bstartwith = true,
                        bendwith = true,
                        binclude = true,
                        bexclude = true,
                        bother = true,
                        buserdefine = false,
                        minLen = 18,
                        maxLen = 100,
                        startwith = "WB",
                        endwith = "",
                        include = "",
                        include_start = 0,
                        include_end = 0,
                        exclude = "",
                        exclude_start = 0,
                        exclude_end = 0,
                        other = 2
                    },
                    enable = true
                }
            }
        };
        
        Log.Information("已初始化条码规则，共 {Count} 条规则，其中 {EnabledCount} 条已启用",
            _codeRules.coderules.Count,
            _codeRules.coderules.Count(r => r.enable));
    }

    private class CameraInstance
    {
        public MvCodeReader Camera { get; init; } = null!;
        public Thread? ReceiveThread { get; set; }
        public bool IsGrabbing { get; set; }
        public byte[] ImageBuffer { get; } = new byte[1024 * 1024 * 20];
        public MvCodeReader.cbOutputEx2delegate? ImageCallback { get; set; }
    }

    /// <summary>
    ///     相机是否已连接
    /// </summary>
    public bool IsConnected { get; private set; }

    /// <summary>
    ///     包裹信息流
    /// </summary>
    public IObservable<PackageInfo> PackageStream =>
        _packageSubject.AsObservable()
            .ObserveOn(TaskPoolScheduler.Default)
            .Publish()
            .RefCount();

    /// <summary>
    ///     图像信息流
    /// </summary>
    public IObservable<(Image<Rgba32> image, IReadOnlyList<BarcodeLocation> barcodes)> ImageStream =>
        _imageSubject.AsObservable()
            .ObserveOn(TaskPoolScheduler.Default)
            .Publish()
            .RefCount();

    /// <summary>
    ///     相机连接状态改变事件
    /// </summary>
    public event Action<string, bool>? ConnectionChanged;

    /// <summary>
    ///     启动相机服务
    /// </summary>
    public bool Start()
    {
        try
        {
            // 枚举设备并查找目标相机
            var deviceList = new MvCodeReader.MV_CODEREADER_DEVICE_INFO_LIST();
            var nRet = MvCodeReader.MV_CODEREADER_EnumDevices_NET(ref deviceList, MvCodeReader.MV_CODEREADER_GIGE_DEVICE);
            if (nRet != MvCodeReader.MV_CODEREADER_OK)
            {
                Log.Error("枚举设备失败 (错误码: 0x{Code:X})", nRet);
                return false;
            }

            if (deviceList.nDeviceNum == 0)
            {
                Log.Error("未发现任何相机");
                return false;
            }

            // 更新配置中的相机列表
            var cameraInfos = new List<DeviceCameraInfo>();
            for (var i = 0; i < deviceList.nDeviceNum; i++)
            {
                var deviceInfo = (MvCodeReader.MV_CODEREADER_DEVICE_INFO)Marshal.PtrToStructure(
                    deviceList.pDeviceInfo[i],
                    typeof(MvCodeReader.MV_CODEREADER_DEVICE_INFO))!;

                if (deviceInfo.nTLayerType == MvCodeReader.MV_CODEREADER_GIGE_DEVICE)
                {
                    // 获取GigE设备的特殊信息
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(deviceInfo.SpecialInfo.stGigEInfo, 0);
                    var gigEInfo = (MvCodeReader.MV_CODEREADER_GIGE_DEVICE_INFO)Marshal.PtrToStructure(
                        buffer,
                        typeof(MvCodeReader.MV_CODEREADER_GIGE_DEVICE_INFO))!;

                    var cameraInfo = new DeviceCameraInfo { Index = i };
                    
                    // 获取正确的IP地址
                    var deviceIp = $"{gigEInfo.nCurrentIp & 0xFF}.{(gigEInfo.nCurrentIp >> 8) & 0xFF}.{(gigEInfo.nCurrentIp >> 16) & 0xFF}.{(gigEInfo.nCurrentIp >> 24) & 0xFF}";
                    cameraInfo.IpAddress = deviceIp;  // 设置正确的IP地址
                    cameraInfo.UpdateFromDeviceInfo(deviceInfo);

                    // 记录更详细的设备信息
                    string deviceName;
                    if (!string.IsNullOrEmpty(gigEInfo.chUserDefinedName))
                    {
                        deviceName = $"GEV: {gigEInfo.chUserDefinedName} ({gigEInfo.chSerialNumber})";
                    }
                    else
                    {
                        deviceName = $"GEV: {gigEInfo.chManufacturerName} {gigEInfo.chModelName} ({gigEInfo.chSerialNumber})";
                    }

                    Log.Information("发现相机: {Name}", deviceName);
                    Log.Debug("相机详细信息: IP={IP}, MAC={MAC:X8}{MAC2:X8}, 制造商={Manufacturer}, 型号={Model}, 序列号={Serial}",
                        deviceIp,
                        deviceInfo.nMacAddrHigh,
                        deviceInfo.nMacAddrLow,
                        gigEInfo.chManufacturerName,
                        gigEInfo.chModelName,
                        gigEInfo.chSerialNumber);

                    cameraInfos.Add(cameraInfo);
                }
            }

            _settings.SelectedCameras = cameraInfos;
            Log.Information("发现 {Count} 个相机", cameraInfos.Count);

            // 遍历并连接所有相机
            foreach (var targetCamera in cameraInfos)
            {
                string deviceIp = targetCamera.IpAddress;  // 使用targetCamera中的IP地址作为初始值
                try
                {
                    // 创建相机实例
                    var instance = new CameraInstance
                    {
                        Camera = new MvCodeReader()
                    };

                    // 获取目标设备信息
                    var deviceInfo = (MvCodeReader.MV_CODEREADER_DEVICE_INFO)Marshal.PtrToStructure(
                        deviceList.pDeviceInfo[targetCamera.Index],
                        typeof(MvCodeReader.MV_CODEREADER_DEVICE_INFO))!;

                    // 获取GigE设备的特殊信息以获取正确的IP地址
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(deviceInfo.SpecialInfo.stGigEInfo, 0);
                    var gigEInfo = (MvCodeReader.MV_CODEREADER_GIGE_DEVICE_INFO)Marshal.PtrToStructure(
                        buffer,
                        typeof(MvCodeReader.MV_CODEREADER_GIGE_DEVICE_INFO))!;

                    deviceIp = $"{gigEInfo.nCurrentIp & 0xFF}.{(gigEInfo.nCurrentIp >> 8) & 0xFF}.{(gigEInfo.nCurrentIp >> 16) & 0xFF}.{(gigEInfo.nCurrentIp >> 24) & 0xFF}";

                    // 检查设备访问权限
                    if (!MvCodeReader.MV_CODEREADER_IsDeviceAccessible_NET(ref deviceInfo, MvCodeReader.MV_CODEREADER_ACCESS_Exclusive))
                    {
                        Log.Error("相机 {IP} 当前不可访问，可能被其他程序占用", deviceIp);
                        continue;
                    }

                    // 创建句柄
                    nRet = instance.Camera.MV_CODEREADER_CreateHandle_NET(ref deviceInfo);
                    if (nRet != MvCodeReader.MV_CODEREADER_OK)
                    {
                        Log.Error("创建相机 {IP} 的句柄失败", deviceIp);
                        continue;
                    }

                    // 打开设备
                    const int maxRetries = 3;
                    const int retryDelayMs = 1000;
                    bool deviceOpened = false;

                    for (int retry = 0; retry < maxRetries; retry++)
                    {
                        if (retry > 0)
                        {
                            Log.Information("正在尝试第 {Retry} 次打开相机 {IP}", retry + 1, deviceIp);
                            Thread.Sleep(retryDelayMs);
                        }

                        nRet = instance.Camera.MV_CODEREADER_OpenDevice_NET();
                        if (nRet == MvCodeReader.MV_CODEREADER_OK)
                        {
                            deviceOpened = true;
                            break;
                        }

                        string errorMsg;
                        switch (nRet)
                        {
                            case MvCodeReader.MV_CODEREADER_E_HANDLE:
                                errorMsg = "无效的句柄";
                                break;
                            case MvCodeReader.MV_CODEREADER_E_SUPPORT:
                                errorMsg = "不支持的功能";
                                break;
                            case MvCodeReader.MV_CODEREADER_E_BUSY:
                                errorMsg = "设备忙，可能被其他程序占用";
                                break;
                            case MvCodeReader.MV_CODEREADER_E_NETER:
                                errorMsg = "网络错误";
                                break;
                            case MvCodeReader.MV_CODEREADER_E_PARAMETER:
                                errorMsg = "参数错误";
                                break;
                            case MvCodeReader.MV_CODEREADER_E_RESOURCE:
                                errorMsg = "资源申请失败";
                                break;
                            case MvCodeReader.MV_CODEREADER_E_NODATA:
                                errorMsg = "无数据";
                                break;
                            case MvCodeReader.MV_CODEREADER_E_PRECONDITION:
                                errorMsg = "前置条件有误，或者依赖的资源未就绪";
                                break;
                            case MvCodeReader.MV_CODEREADER_E_VERSION:
                                errorMsg = "版本不匹配";
                                break;
                            case MvCodeReader.MV_CODEREADER_E_NOENOUGH_BUF:
                                errorMsg = "缓存不足";
                                break;
                            case MvCodeReader.MV_CODEREADER_E_UNKNOW:
                                errorMsg = "未知的错误";
                                break;
                            case MvCodeReader.MV_CODEREADER_E_GC_GENERIC:
                                errorMsg = "通用错误";
                                break;
                            case MvCodeReader.MV_CODEREADER_E_GC_ACCESS:
                                errorMsg = "访问权限错误";
                                break;
                            case MvCodeReader.MV_CODEREADER_E_ACCESS_DENIED:
                                errorMsg = "拒绝访问";
                                break;
                            case MvCodeReader.MV_CODEREADER_E_USB_DRIVER:
                                errorMsg = "USB驱动错误";
                                break;
                            case MvCodeReader.MV_CODEREADER_E_USB_BANDWIDTH:
                                errorMsg = "USB带宽不足";
                                break;
                            case MvCodeReader.MV_CODEREADER_E_USB_DEVICE:
                                errorMsg = "USB设备错误";
                                break;
                            case MvCodeReader.MV_CODEREADER_E_NET_TIMEOUT:
                                errorMsg = "网络连接超时";
                                break;
                            default:
                                errorMsg = $"未知错误";
                                break;
                        }
                        Log.Error("打开相机 {IP} 失败: {Error} (错误码: 0x{Code:X})", deviceIp, errorMsg, nRet);
                    }

                    if (!deviceOpened)
                    {
                        Log.Error("多次尝试后仍无法打开相机 {IP}", deviceIp);
                        continue;
                    }
                    
                    // 注册回调函数 - 在开始采集之前注册
                    instance.IsGrabbing = true;
                    Log.Information("相机 {IP}: 注册图像回调函数", deviceIp);
                    
                    // 使用Ex2版本的回调
                    instance.ImageCallback = new MvCodeReader.cbOutputEx2delegate((IntPtr pData, IntPtr pstFrameInfoEx2, IntPtr pUser) =>
                    {
                        try
                        {
                            if (pData == IntPtr.Zero || pstFrameInfoEx2 == IntPtr.Zero)
                            {
                                Log.Error("回调参数无效：pData={Data}, pstFrameInfoEx2={FrameInfo}", 
                                    pData == IntPtr.Zero ? "NULL" : "Valid",
                                    pstFrameInfoEx2 == IntPtr.Zero ? "NULL" : "Valid");
                                return;
                            }

                            var ipAddress = deviceIp;  // 使用正确的IP地址
                            
                            // 将帧信息转换为结构体
                            var frameInfo = (MvCodeReader.MV_CODEREADER_IMAGE_OUT_INFO_EX2)Marshal.PtrToStructure(
                                pstFrameInfoEx2,
                                typeof(MvCodeReader.MV_CODEREADER_IMAGE_OUT_INFO_EX2))!;
                            
                            Log.Debug("相机 {IP}: 收到图像回调 (Ex2)", ipAddress);
                            Log.Debug("相机 {IP}: 图像信息 - 宽度:{Width}, 高度:{Height}, 帧号:{FrameNum}", 
                                ipAddress, frameInfo.nWidth, frameInfo.nHeight, frameInfo.nFrameNum);
                                
                            if (frameInfo.nFrameLen <= 0)
                            {
                                Log.Warning("相机 {IP}: 获取到的帧长度为0", ipAddress);
                                return;
                            }

                            Log.Debug("相机 {IP}: 成功获取一帧图像", ipAddress);
                            Log.Debug("相机 {IP}: 图像大小 {Size} 字节", ipAddress, frameInfo.nFrameLen);
                            
                            // 确保缓冲区大小足够
                            if (frameInfo.nFrameLen > instance.ImageBuffer.Length)
                            {
                                Log.Warning("相机 {IP}: 图像数据大小({Size})超过缓冲区大小({BufferSize})", 
                                    ipAddress, frameInfo.nFrameLen, instance.ImageBuffer.Length);
                                return;
                            }

                            // 创建临时缓冲区并复制数据
                            byte[] tempBuffer = new byte[frameInfo.nFrameLen];
                            Marshal.Copy(pData, tempBuffer, 0, (int)frameInfo.nFrameLen);
                            
                            // 处理条码结果
                            string barcode = "NoRead";
                            if (frameInfo.UnparsedBcrList.pstCodeListEx2 != IntPtr.Zero)
                            {
                                try
                                {
                                    var bcrResult = (MvCodeReader.MV_CODEREADER_RESULT_BCR_EX2)Marshal.PtrToStructure(
                                        frameInfo.UnparsedBcrList.pstCodeListEx2,
                                        typeof(MvCodeReader.MV_CODEREADER_RESULT_BCR_EX2))!;

                                    if (bcrResult.nCodeNum > 0 && bcrResult.stBcrInfoEx2 != null && bcrResult.stBcrInfoEx2.Length > 0)
                                    {
                                        // 检查编码并正确解码
                                        string decodedBarcode;
                                        if (IsTextUTF8(bcrResult.stBcrInfoEx2[0].chCode))
                                        {
                                            decodedBarcode = Encoding.UTF8.GetString(bcrResult.stBcrInfoEx2[0].chCode).Trim().TrimEnd('\0');
                                        }
                                        else
                                        {
                                            try
                                            {
                                                decodedBarcode = Encoding.GetEncoding("GB2312").GetString(bcrResult.stBcrInfoEx2[0].chCode).Trim().TrimEnd('\0');
                                            }
                                            catch (Exception ex)
                                            {
                                                Log.Warning(ex, "使用GB2312解码失败，尝试使用默认编码");
                                                decodedBarcode = Encoding.Default.GetString(bcrResult.stBcrInfoEx2[0].chCode).Trim().TrimEnd('\0');
                                            }
                                        }

                                        // 验证条码是否符合规则
                                        if (_codeRules.IsValidBarcode(decodedBarcode))
                                        {
                                            barcode = decodedBarcode;
                                            Log.Information("相机 {IP}: 条码 {Barcode} 符合规则要求", ipAddress, barcode);
                                        }
                                        else
                                        {
                                            Log.Warning("相机 {IP}: 条码 {Barcode} 不符合规则要求，标记为NoRead", ipAddress, decodedBarcode);
                                            barcode = "NoRead";
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, "相机 {IP}: 处理条码数据时发生错误", ipAddress);
                                }
                            }
                            
                            Log.Debug("相机 {IP}: 处理条码结果: {Barcode}", ipAddress, barcode);
                            
                            // 处理条码结果，传入相机IP地址
                            ProcessBarcodeResult(barcode, frameInfo, tempBuffer, ipAddress);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "图像回调函数处理时发生错误");
                        }
                    });
                    
                    // 注册Ex2回调
                    nRet = instance.Camera.MV_CODEREADER_RegisterImageCallBackEx2_NET(instance.ImageCallback, IntPtr.Zero);
                    if (nRet != MvCodeReader.MV_CODEREADER_OK)
                    {
                        string errorMsg;
                        switch (nRet)
                        {
                            case MvCodeReader.MV_CODEREADER_E_HANDLE:
                                errorMsg = "无效的句柄";
                                break;
                            case MvCodeReader.MV_CODEREADER_E_SUPPORT:
                                errorMsg = "不支持的功能";
                                break;
                            case MvCodeReader.MV_CODEREADER_E_PARAMETER:
                                errorMsg = "参数错误";
                                break;
                            default:
                                errorMsg = $"未知错误 (错误码: 0x{nRet:X})";
                                break;
                        }
                        Log.Error("相机 {IP} 注册回调函数失败: {Error} (错误码: 0x{Code:X})", deviceIp, errorMsg, nRet);
                        Log.Warning("相机 {IP}: 将使用轮询模式获取图像", deviceIp);
                    }
                    else
                    {
                        Log.Information("相机 {IP}: 图像回调函数已注册", deviceIp);
                    }

                    // 开始采集
                    nRet = instance.Camera.MV_CODEREADER_StartGrabbing_NET();
                    if (nRet != MvCodeReader.MV_CODEREADER_OK)
                    {
                        Log.Error("相机 {IP} 开始采集失败", deviceIp);
                        continue;
                    }
                    
                    // 如果回调注册失败，使用轮询模式
                    if (instance.ImageCallback == null)
                    {
                        Log.Information("相机 {IP}: 使用轮询模式获取图像", deviceIp);
                        instance.ReceiveThread = new Thread(() => ReceiveImages(deviceIp, instance));
                        instance.ReceiveThread.Start();
                        Log.Information("相机 {IP}: 图像接收线程已启动", deviceIp);
                    }

                    // 添加到相机字典
                    _cameras[deviceIp] = instance;

                    IsConnected = true;
                    ConnectionChanged?.Invoke(deviceIp, true);
                    Log.Information("相机 {IP} 已连接并开始采集", deviceIp);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "连接相机 {IP} 时发生错误", deviceIp);
                }
            }

            return IsConnected; // 只要有一个相机连接成功就返回true
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动相机服务时发生错误");
            return false;
        }
    }

    /// <summary>
    ///     停止相机服务
    /// </summary>
    public bool Stop()
    {
        try
        {
            foreach (var (ip, instance) in _cameras)
            {
                try
                {
                    Log.Information("相机 {IP}: 准备停止采集", ip);
                    instance.IsGrabbing = false;

                    // 停止采集
                    Log.Debug("相机 {IP}: 调用 MV_CODEREADER_StopGrabbing_NET", ip);
                    instance.Camera.MV_CODEREADER_StopGrabbing_NET();

                    // 关闭设备
                    Log.Debug("相机 {IP}: 调用 MV_CODEREADER_CloseDevice_NET", ip);
                    instance.Camera.MV_CODEREADER_CloseDevice_NET();

                    // 销毁句柄
                    Log.Debug("相机 {IP}: 调用 MV_CODEREADER_DestroyHandle_NET", ip);
                    instance.Camera.MV_CODEREADER_DestroyHandle_NET();

                    Log.Information("相机 {IP} 已停止采集", ip);

                    // 更新连接状态
                    ConnectionChanged?.Invoke(ip, false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "停止相机 {IP} 时发生错误", ip);
                }
            }

            _cameras.Clear();
            IsConnected = false;

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止采集时发生错误");
            return false;
        }
    }

    /// <summary>
    ///     获取相机信息列表
    /// </summary>
    public IEnumerable<DeviceCameraInfo> GetCameraInfos()
    {
        MvCodeReader.MV_CODEREADER_DEVICE_INFO_LIST deviceList = new();
        List<DeviceCameraInfo> result = [];

        try
        {
            GC.Collect();
            var nRet = MvCodeReader.MV_CODEREADER_EnumDevices_NET(ref deviceList, MvCodeReader.MV_CODEREADER_GIGE_DEVICE);
            if (nRet != MvCodeReader.MV_CODEREADER_OK)
            {
                Log.Error("枚举设备失败 (错误码: 0x{Code:X})", nRet);
                return result;
            }

            Log.Information("发现 {Count} 个设备", deviceList.nDeviceNum);

            for (var i = 0; i < deviceList.nDeviceNum; i++)
            {
                var deviceInfo = (MvCodeReader.MV_CODEREADER_DEVICE_INFO)Marshal.PtrToStructure(
                    deviceList.pDeviceInfo[i],
                    typeof(MvCodeReader.MV_CODEREADER_DEVICE_INFO))!;

                if (deviceInfo.nTLayerType == MvCodeReader.MV_CODEREADER_GIGE_DEVICE)
                {
                    // 获取GigE设备的特殊信息
                    IntPtr buffer = Marshal.UnsafeAddrOfPinnedArrayElement(deviceInfo.SpecialInfo.stGigEInfo, 0);
                    var gigEInfo = (MvCodeReader.MV_CODEREADER_GIGE_DEVICE_INFO)Marshal.PtrToStructure(
                        buffer, 
                        typeof(MvCodeReader.MV_CODEREADER_GIGE_DEVICE_INFO))!;

                    var cameraInfo = new DeviceCameraInfo { Index = i };
                    cameraInfo.UpdateFromDeviceInfo(deviceInfo);

                    // 记录更详细的设备信息
                    string deviceName;
                    if (!string.IsNullOrEmpty(gigEInfo.chUserDefinedName))
                    {
                        deviceName = $"GEV: {gigEInfo.chUserDefinedName} ({gigEInfo.chSerialNumber})";
                    }
                    else
                    {
                        deviceName = $"GEV: {gigEInfo.chManufacturerName} {gigEInfo.chModelName} ({gigEInfo.chSerialNumber})";
                    }

                    Log.Information("发现相机: {Name}", deviceName);
                    Log.Debug("相机详细信息: IP={IP}, MAC={MAC:X8}{MAC2:X8}, 制造商={Manufacturer}, 型号={Model}",
                        cameraInfo.IpAddress,
                        deviceInfo.nMacAddrHigh,
                        deviceInfo.nMacAddrLow,
                        gigEInfo.chManufacturerName,
                        gigEInfo.chModelName);

                    result.Add(cameraInfo);
                }
            }

            // 如果发现多个相机具有相同的IP地址，记录警告
            var duplicateIPs = result.GroupBy(x => x.IpAddress)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateIPs.Any())
            {
                foreach (var ip in duplicateIPs)
                {
                    var cameras = result.Where(x => x.IpAddress == ip).ToList();
                    Log.Warning("发现多个相机使用相同的IP地址 {IP}:", ip);
                    foreach (var camera in cameras)
                    {
                        Log.Warning("  - 相机: {Name} (SN: {Serial})",
                            camera.Model, camera.SerialNumber);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "枚举设备时发生错误");
        }

        return result;
    }

    /// <summary>
    ///     更新相机配置
    /// </summary>
    public void UpdateConfiguration(CameraSettings config)
    {
        _settings = config;
        Log.Information("相机配置已更新");
    }

    /// <summary>
    ///     设置条码规则
    /// </summary>
    /// <param name="rules">条码规则配置</param>
    public void SetCodeRules(CodeRules rules)
    {
        _codeRules = rules;
        Log.Information("已更新条码规则配置，共 {Count} 条规则，其中 {EnabledCount} 条已启用",
            rules.coderules.Count,
            rules.coderules.Count(r => r.enable));
    }

    /// <summary>
    ///     释放资源
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            Stop();
            _packageSubject.Dispose();
            _imageSubject.Dispose();

            Log.Information("相机资源已释放");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放相机资源时发生错误");
        }

        _disposed = true;
    }

    private void ReceiveImages(string ipAddress, CameraInstance instance)
    {
        Log.Information("开始接收相机 {IP} 的图像", ipAddress);
        int errorCount = 0;
        DateTime lastLogTime = DateTime.Now;
        
        while (instance.IsGrabbing)
        {
            try
            {
                var pData = IntPtr.Zero;
                MvCodeReader.MV_CODEREADER_IMAGE_OUT_INFO_EX2 stFrameInfo = new();
                var pFrameInfo =
                    Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MvCodeReader.MV_CODEREADER_IMAGE_OUT_INFO_EX2)));
                Marshal.StructureToPtr(stFrameInfo, pFrameInfo, false);

                var nRet = instance.Camera.MV_CODEREADER_MSC_GetOneFrameTimeout_NET(
                    ref pData,
                    pFrameInfo,
                    0, // 通道0
                    1000); // 超时时间1000ms

                if (nRet == MvCodeReader.MV_CODEREADER_OK)
                {
                    stFrameInfo = (MvCodeReader.MV_CODEREADER_IMAGE_OUT_INFO_EX2)Marshal.PtrToStructure(
                        pFrameInfo,
                        typeof(MvCodeReader.MV_CODEREADER_IMAGE_OUT_INFO_EX2))!;

                    if (stFrameInfo.nFrameLen > 0)
                    {
                        Marshal.Copy(pData, instance.ImageBuffer, 0, (int)stFrameInfo.nFrameLen);
                        var barcode = ProcessBarcode(stFrameInfo);
                        
                        // 处理所有条码结果，包括 NoRead
                        if (!string.IsNullOrEmpty(barcode))
                        {
                            if (barcode != "NoRead")
                            {
                                Log.Information("相机 {IP}: 识别到有效条码: {Barcode}", ipAddress, barcode);
                            }
                            else
                            {
                                Log.Debug("相机 {IP}: 未识别到条码，发布 NoRead 事件", ipAddress);
                            }
                            ProcessBarcodeResult(barcode, stFrameInfo, instance.ImageBuffer, ipAddress);
                        }
                    }
                    else
                    {
                        Log.Warning("相机 {IP}: 获取到的帧长度为0", ipAddress);
                    }
                }
                else
                {
                    // 记录获取图像失败的错误
                    errorCount++;
                    string errorMsg;
                    switch (nRet)
                    {
                        case MvCodeReader.MV_CODEREADER_E_NET_TIMEOUT:
                            errorMsg = "获取图像超时";
                            break;
                        case MvCodeReader.MV_CODEREADER_E_HANDLE:
                            errorMsg = "无效的句柄";
                            break;
                        case MvCodeReader.MV_CODEREADER_E_SUPPORT:
                            errorMsg = "不支持的功能";
                            break;
                        case MvCodeReader.MV_CODEREADER_E_NODATA:
                            errorMsg = "无数据";
                            break;
                        default:
                            errorMsg = $"未知错误 (错误码: 0x{nRet:X})";
                            break;
                    }
                    
                    // 每分钟最多记录一次错误，避免日志过多
                    if ((DateTime.Now - lastLogTime).TotalMinutes >= 1)
                    {
                        Log.Warning("相机 {IP}: 获取图像失败: {Error}, 累计错误次数: {Count}", ipAddress, errorMsg, errorCount);
                        lastLogTime = DateTime.Now;
                        errorCount = 0;
                    }
                }

                Marshal.FreeHGlobal(pFrameInfo);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "接收相机 {IP} 图像时发生错误", ipAddress);
                Thread.Sleep(100);
            }
        }
        
        Log.Information("相机 {IP} 的图像接收线程已退出", ipAddress);
    }

    private string ProcessBarcode(MvCodeReader.MV_CODEREADER_IMAGE_OUT_INFO_EX2 frameInfo)
    {
        if (frameInfo.UnparsedBcrList.pstCodeListEx2 == IntPtr.Zero) return "NoRead";

        var bcrResult = (MvCodeReader.MV_CODEREADER_RESULT_BCR_EX2)Marshal.PtrToStructure(
            frameInfo.UnparsedBcrList.pstCodeListEx2,
            typeof(MvCodeReader.MV_CODEREADER_RESULT_BCR_EX2))!;

        // 如果没有识别到条码，返回NoRead
        if (bcrResult.nCodeNum <= 0) return "NoRead";

        // 遍历所有条码
        for (int i = 0; i < bcrResult.nCodeNum; i++)
        {
            try
            {
                string decodedBarcode;
                if (IsTextUTF8(bcrResult.stBcrInfoEx2[i].chCode))
                {
                    decodedBarcode = Encoding.UTF8.GetString(bcrResult.stBcrInfoEx2[i].chCode).Trim().TrimEnd('\0');
                }
                else
                {
                    try
                    {
                        decodedBarcode = Encoding.GetEncoding("GB2312").GetString(bcrResult.stBcrInfoEx2[i].chCode).Trim().TrimEnd('\0');
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "使用GB2312解码失败，尝试使用默认编码");
                        decodedBarcode = Encoding.Default.GetString(bcrResult.stBcrInfoEx2[i].chCode).Trim().TrimEnd('\0');
                    }
                }

                // 验证条码是否符合规则
                if (_codeRules.IsValidBarcode(decodedBarcode))
                {
                    Log.Information("找到符合规则的条码: {Barcode}", decodedBarcode);
                    return decodedBarcode;
                }
                else
                {
                    Log.Debug("条码 {Barcode} 不符合规则要求", decodedBarcode);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理第 {Index} 个条码时发生错误", i + 1);
            }
        }

        // 如果没有找到符合规则的条码，返回NoRead
        Log.Warning("未找到符合规则的条码，共处理 {Count} 个条码", bcrResult.nCodeNum);
        return "NoRead";
    }

    private void ProcessBarcodeResult(string barcode, MvCodeReader.MV_CODEREADER_IMAGE_OUT_INFO_EX2 frameInfo, byte[] imageBuffer, string ipAddress)
    {
        try
        {
            lock (_cacheUpdateLock)
            {
                var now = DateTime.Now;

                // 检查是否需要重置触发计数器（如果距离上次触发时间超过间隔）
                if ((now - _lastTriggerTime).TotalMilliseconds > TriggerIntervalMs)
                {
                    _currentTriggerCount = 0;
                    _cameraBarcodeCaches.Clear();
                    _cameraProcessed.Clear();
                    _lastTriggerTime = now;
                }

                // 更新当前相机的条码缓存
                _cameraBarcodeCaches[ipAddress] = (now, barcode);
                _cameraProcessed[ipAddress] = true;

                // 检查是否所有相机都已处理完毕
                var allCamerasProcessed = _cameras.Count == _cameraProcessed.Count;
                
                if (allCamerasProcessed)
                {
                    _currentTriggerCount++;
                    Log.Debug("当前触发计数：{Count}", _currentTriggerCount);

                    try
                    {
                        // 检查所有相机的条码结果
                        var validBarcode = _cameraBarcodeCaches.Values
                            .Where(cache => cache.Barcode != "NoRead")
                            .Select(cache => cache.Barcode)
                            .FirstOrDefault();

                        // 只在第一次触发时发布数据
                        if (_currentTriggerCount == 1)
                        {
                            // 创建包裹信息
                            var package = new PackageInfo
                            {
                                Barcode = validBarcode ?? "NoRead"
                            };

                            // 处理图像数据
                            using var image = ProcessImage(frameInfo, imageBuffer);
                            if (image != null)
                            {
                                package.Image = image;
                            }
                            else
                            {
                                Log.Warning("无法处理图像数据");
                            }

                            // 发布数据
                            if (validBarcode != null)
                            {
                                Log.Information("发布条码：{Barcode}", validBarcode);
                            }
                            else
                            {
                                Log.Debug("所有相机都未识别到有效条码，发布NoRead");
                            }
                            PublishData(package, image);
                        }
                        else
                        {
                            Log.Debug("跳过重复触发的数据发布，触发计数：{Count}", _currentTriggerCount);
                        }
                    }
                    finally
                    {
                        // 重置相机处理状态
                        _cameraProcessed.Clear();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理条码结果时发生错误");
        }
    }

    private void PublishThrottled(PackageInfo package, Image<Rgba32>? image)
    {
        lock (_publishLock)
        {
            var cameraId = package.Barcode; // 使用条码作为唯一标识
            var now = DateTime.Now;

            if (!_lastPublishTime.TryGetValue(cameraId, out var lastTime) || 
                (now - lastTime).TotalMilliseconds >= ThrottleIntervalMs)
            {
                // 如果超过节流时间间隔，直接发布
                PublishData(package, image);
                _lastPublishTime[cameraId] = now;
                _pendingPackages.Remove(cameraId);
            }
            else
            {
                // 如果在节流时间内，更新待发布的数据
                _pendingPackages[cameraId] = package;
                
                // 启动一个延迟任务来发布待处理的数据
                Task.Delay(ThrottleIntervalMs - (int)(now - lastTime).TotalMilliseconds)
                    .ContinueWith(_ =>
                    {
                        lock (_publishLock)
                        {
                            if (_pendingPackages.TryGetValue(cameraId, out var pendingPackage))
                            {
                                PublishData(pendingPackage, pendingPackage.Image);
                                _lastPublishTime[cameraId] = DateTime.Now;
                                _pendingPackages.Remove(cameraId);
                            }
                        }
                    });
            }
        }
    }

    private void PublishData(PackageInfo package, Image<Rgba32>? image)
    {
        _packageSubject.OnNext(package);
        
        if (image != null)
        {
            _imageSubject.OnNext((image.Clone(), new List<BarcodeLocation>()));
        }
        
        Log.Debug("发布数据：条码 = {Barcode}, 图像大小 = {ImageSize}", 
            package.Barcode,
            image != null ? $"{image.Width}x{image.Height}" : "无图像");
    }

    private Image<Rgba32>? ProcessImage(MvCodeReader.MV_CODEREADER_IMAGE_OUT_INFO_EX2 frameInfo, byte[] imageBuffer)
    {
        try
        {
            switch (frameInfo.enPixelType)
            {
                case MvCodeReader.MvCodeReaderGvspPixelType.PixelType_CodeReader_Gvsp_Mono8:
                {
                    // 处理黑白图像
                    Image<Rgba32> image = new(frameInfo.nWidth, frameInfo.nHeight);

                    // 将8位灰度值转换为32位RGBA值
                    image.ProcessPixelRows(accessor =>
                    {
                        for (var y = 0; y < frameInfo.nHeight; y++)
                        {
                            var row = accessor.GetRowSpan(y);
                            for (var x = 0; x < frameInfo.nWidth; x++)
                            {
                                var gray = imageBuffer[y * frameInfo.nWidth + x];
                                row[x] = new Rgba32(gray, gray, gray, 255);
                            }
                        }
                    });

                    return image;
                }
                case MvCodeReader.MvCodeReaderGvspPixelType.PixelType_CodeReader_Gvsp_Jpeg:
                {
                    // 处理JPEG图像
                    try 
                    {
                        Log.Debug("开始处理JPEG图像数据，数据大小: {Size} 字节", imageBuffer.Length);
                        using var ms = new MemoryStream(imageBuffer);
                        var image = Image.Load<Rgba32>(ms);
                        Log.Debug("JPEG图像加载成功，分辨率: {Width}x{Height}", image.Width, image.Height);
                        return image;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "JPEG图像处理失败");
                        // 如果JPEG处理失败，尝试作为Mono8处理
                        Log.Information("尝试以Mono8格式处理图像");
                        goto case MvCodeReader.MvCodeReaderGvspPixelType.PixelType_CodeReader_Gvsp_Mono8;
                    }
                }
                default:
                    Log.Warning("不支持的像素格式: {PixelType}", frameInfo.enPixelType);
                    return null;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理图像数据时发生错误");
            return null;
        }
    }

    private Image<Rgba32>? ProcessImageFromBuffer(int width, int height, byte[] buffer, MvCodeReader.MvCodeReaderGvspPixelType pixelType)
    {
        try
        {
            switch (pixelType)
            {
                case MvCodeReader.MvCodeReaderGvspPixelType.PixelType_CodeReader_Gvsp_Mono8:
                {
                    // 处理黑白图像
                    var monoImage = new Image<Rgba32>(width, height);

                    // 将8位灰度值转换为32位RGBA值
                    monoImage.ProcessPixelRows(accessor =>
                    {
                        for (var y = 0; y < height; y++)
                        {
                            var row = accessor.GetRowSpan(y);
                            for (var x = 0; x < width; x++)
                            {
                                if (y * width + x < buffer.Length)
                                {
                                    var gray = buffer[y * width + x];
                                    row[x] = new Rgba32(gray, gray, gray, 255);
                                }
                            }
                        }
                    });

                    return monoImage;
                }
                case MvCodeReader.MvCodeReaderGvspPixelType.PixelType_CodeReader_Gvsp_Jpeg:
                {
                    // 处理JPEG图像
                    try 
                    {
                        Log.Debug("开始处理JPEG图像数据，数据大小: {Size} 字节", buffer.Length);
                        using var ms = new MemoryStream(buffer);
                        var image = Image.Load<Rgba32>(ms);
                        Log.Debug("JPEG图像加载成功，分辨率: {Width}x{Height}", image.Width, image.Height);
                        return image;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "JPEG图像处理失败");
                        // 如果JPEG处理失败，尝试作为Mono8处理
                        Log.Information("尝试以Mono8格式处理图像");
                        goto case MvCodeReader.MvCodeReaderGvspPixelType.PixelType_CodeReader_Gvsp_Mono8;
                    }
                }
                default:
                    Log.Warning("不支持的像素格式: {PixelType}", pixelType);
                    
                    // 尝试以 Mono8 格式处理
                    var defaultImage = new Image<Rgba32>(width, height);
                    defaultImage.ProcessPixelRows(accessor =>
                    {
                        for (var y = 0; y < height; y++)
                        {
                            var row = accessor.GetRowSpan(y);
                            for (var x = 0; x < width; x++)
                            {
                                if (y * width + x < buffer.Length)
                                {
                                    var gray = buffer[y * width + x];
                                    row[x] = new Rgba32(gray, gray, gray, 255);
                                }
                            }
                        }
                    });
                    return defaultImage;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理图像数据时发生错误");
            return null;
        }
    }

    private static bool IsTextUTF8(byte[] inputStream)
    {
        int encodingBytesCount = 0;
        bool allTextsAreASCIIChars = true;

        for (int i = 0; i < inputStream.Length; i++)
        {
            byte current = inputStream[i];

            if ((current & 0x80) == 0x80)
            {
                allTextsAreASCIIChars = false;
            }
            // First byte
            if (encodingBytesCount == 0)
            {
                if ((current & 0x80) == 0)
                {
                    // ASCII chars, from 0x00-0x7F
                    continue;
                }

                if ((current & 0xC0) == 0xC0)
                {
                    encodingBytesCount = 1;
                    current <<= 2;

                    // More than two bytes used to encoding a unicode char.
                    // Calculate the real length.
                    while ((current & 0x80) == 0x80)
                    {
                        current <<= 1;
                        encodingBytesCount++;
                    }
                }
                else
                {
                    // Invalid bits structure for UTF8 encoding rule.
                    return false;
                }
            }
            else
            {
                // Following bytes, must start with 10.
                if ((current & 0xC0) == 0x80)
                {
                    encodingBytesCount--;
                }
                else
                {
                    // Invalid bits structure for UTF8 encoding rule.
                    return false;
                }
            }
        }

        if (encodingBytesCount != 0)
        {
            // Invalid bits structure for UTF8 encoding rule.
            // Wrong following bytes count.
            return false;
        }

        // Although UTF8 supports encoding for ASCII chars, we regard as a input stream, whose contents are all ASCII as default encoding.
        return !allTextsAreASCIIChars;
    }
} 
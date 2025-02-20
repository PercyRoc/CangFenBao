using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using CommonLibrary.Models;
using CommonLibrary.Models.Settings.Camera;
using DeviceService.Camera.Models;
using MVIDCodeReaderNet;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Runtime.CompilerServices;

namespace DeviceService.Camera.Hikvision;

/// <summary>
///     海康工业相机SDK客户端
/// </summary>
public class HikvisionIndustrialCameraSdkClient : ICameraService
{
    private const int MaxConcurrentProcessing = 3;

    // 保持委托的引用，防止被GC回收
    private readonly MVIDCodeReader.cbOutputdelegate _imageCallback;
    private readonly Channel<(IntPtr imageData, int imageSize)> _imageChannel;

    private readonly Subject<PackageInfo> _packageSubject = new();
    private readonly CancellationTokenSource _processingCancellation = new();
    private readonly SemaphoreSlim _processingSemaphore;

    private readonly Subject<(Image<Rgba32> image, IReadOnlyList<BarcodeLocation> barcodes)>
        _realtimeImageSubject = new();

    // 添加SDK调用锁
    private readonly object _sdkLock = new();

    private MVIDCodeReader? _device;
    private MVIDCodeReader.MVID_CAMERA_INFO_LIST _deviceList;
    private GCHandle _imageCallbackHandle;
    private ushort _imageHeight;
    private ushort _imageWidth;
    private bool _isGrabbing;
    private int _processingCount;
    private Task? _processingTask;
    
    // 添加相机配置字段
    private CameraSettings _configuration = new();
    private string? _deviceIdentifier;

    /// <summary>
    ///     初始化海康工业相机SDK客户端
    /// </summary>
    public HikvisionIndustrialCameraSdkClient()
    {
        // 创建有限容量的通道，控制积压
        _imageChannel = Channel.CreateBounded<(IntPtr imageData, int imageSize)>(new BoundedChannelOptions(5)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

        // 初始化信号量
        _processingSemaphore = new SemaphoreSlim(MaxConcurrentProcessing);
        _processingCount = 0;

        // 初始化回调委托并保持引用
        _imageCallback = (pstOutput, _) =>
        {
            try
            {
                // 获取图像信息
                var imageInfo = (MVIDCodeReader.MVID_CAM_OUTPUT_INFO)Marshal.PtrToStructure(
                    pstOutput,
                    typeof(MVIDCodeReader.MVID_CAM_OUTPUT_INFO))!;

                // 处理图像数据
                OnImageCallback(imageInfo.stImage.pImageBuf, (int)imageInfo.stImage.nImageLen);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理图像回调时发生错误");
            }
        };

        _imageCallbackHandle = GCHandle.Alloc(_imageCallback);
        _device = new MVIDCodeReader();

        // 启动处理线程
        StartProcessingThread();
    }

    public bool IsConnected { get; private set; }

    /// <summary>
    ///     包裹信息流
    /// </summary>
    public IObservable<PackageInfo> PackageStream =>
        _packageSubject.AsObservable();

    /// <summary>
    ///     图像信息流
    /// </summary>
    public IObservable<(Image<Rgba32> image, IReadOnlyList<BarcodeLocation> barcodes)> ImageStream =>
        _realtimeImageSubject.AsObservable();

    public event Action<string, bool>? ConnectionChanged;

    public bool Start()
    {
        try
        {
            Log.Information("开始采集...");

            // 1. 检查是否已经在采集
            if (_isGrabbing)
            {
                Log.Information("相机已经在采集中");
                return true;
            }

            // 2. 确保设备已经绑定
            if (_device == null)
            {
                Log.Warning("设备未初始化，尝试重新初始化...");
                ConnectDeviceInternalAsync().Wait();
            }
            // 3. 检查设备是否已经绑定
            if (_device == null) throw new InvalidOperationException("设备未正确初始化，无法开始采集");

            // 4. 尝试开始采集，最多重试3次
            const int maxRetries = 3;
            for (var i = 1; i <= maxRetries; i++)
            {
                try
                {
                    var result = _device.MVID_CR_CAM_StartGrabbing_NET();
                    if (result == MVIDCodeReader.MVID_CR_OK)
                    {
                        _isGrabbing = true;
                        Log.Information("开始采集成功");
                        return true;
                    }

                    Log.Warning("开始采集失败（第{Attempt}次尝试）：0x{Result:X}, {Message}",
                        i, result, GetErrorMessage(result));

                    // 如果是调用顺序错误，尝试重新初始化
                    if (unchecked((int)0x80000003) == result) // MVID_CR_E_CALLORDER
                    {
                        Log.Information("检测到调用顺序错误，尝试重新初始化...");
                        StopGrabbingInternalAsync().Wait();
                        ConnectDeviceInternalAsync().Wait();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "开始采集时发生异常（第{Attempt}次尝试）", i);
                    if (i == maxRetries) throw;
                }

                // 在重试之前等待一小段时间
                if (i < maxRetries) Thread.Sleep(500);
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动相机服务失败");
            return false;
        }
    }

    /// <summary>
    /// 执行软触发并返回图像
    /// </summary>
    /// <returns>触发成功返回图像数据，失败返回 null</returns>
    public Image<Rgba32>? ExecuteSoftTrigger()
    {
        Log.Debug("正在执行软触发...");

        var tcs = new TaskCompletionSource<Image<Rgba32>?>();
        IDisposable? subscription = null;

        try
        {
            // 订阅图像流
            subscription = ImageStream.Take(1).Subscribe(imageData =>
            {
                try
                {
                    var clonedImage = imageData.image.Clone();
                    tcs.TrySetResult(clonedImage);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            // 执行软触发
            var result = SafeSdkCall(() =>
            {
                lock (_sdkLock)
                {
                    if (_device == null)
                    {
                        Log.Error("设备未初始化");
                        return false;
                    }

                    var triggerResult = _device.MVID_CR_CAM_SetCommandValue_NET("TriggerSoftware");
                    if (triggerResult == MVIDCodeReader.MVID_CR_OK) return true;
                    Log.Error("软触发失败：{Error}", GetErrorMessage(triggerResult));
                    return false;
                }
            });

            if (!result)
            {
                subscription.Dispose();
                return null;
            }

            // 等待图像数据（最多5秒）
            if (Task.WaitAny([tcs.Task], 5000) != -1) return tcs.Task.Result;
            Log.Warning("等待图像数据超时");
            return null;

        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行软触发时发生错误");
            return null;
        }
        finally
        {
            subscription?.Dispose();
        }
    }

    public void Stop()
    {
        try
        {
            if (!_isGrabbing || _device == null) return;

            Log.Information("正在停止采集...");

            var result = _device.MVID_CR_CAM_StopGrabbing_NET();
            if (result != MVIDCodeReader.MVID_CR_OK)
            {
                Log.Error("停止采集失败：0x{Error:X}, {Message}", result, GetErrorMessage(result));
                return;
            }

            _isGrabbing = false;
            Log.Information("停止采集成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止采集时发生错误");
        }
    }

    public IEnumerable<DeviceCameraInfo>? GetCameraInfos()
    {
        try
        {
            Log.Information("正在枚举海康工业相机...");

            // 枚举设备
            var nRet = MVIDCodeReader.MVID_CR_CAM_EnumDevices_NET(ref _deviceList);
            if (nRet != MVIDCodeReader.MVID_CR_OK)
            {
                Log.Error("枚举设备失败：{Error}", nRet);
                return null;
            }

            if (_deviceList.nDeviceNum == 0)
            {
                Log.Warning("未发现任何相机");
                return [];
            }

            // 转换为通用相机信息
            var cameras = new List<DeviceCameraInfo>();
            for (var i = 0; i < _deviceList.nDeviceNum; i++)
            {
                var deviceInfo = (MVIDCodeReader.MVID_CAMERA_INFO)Marshal.PtrToStructure(
                    _deviceList.pstCamInfo[i],
                    typeof(MVIDCodeReader.MVID_CAMERA_INFO))!;

                var info = new DeviceCameraInfo
                {
                    SerialNumber = deviceInfo.chSerialNumber.Trim('\0'),
                    Model = deviceInfo.chModelName.Trim('\0')
                };

                if (deviceInfo.nCamType == MVIDCodeReader.MVID_GIGE_CAM)
                {
                    info.IpAddress = deviceInfo.ToString()
                        ?.Split(',')
                        .FirstOrDefault(x => x.Contains("IP"))?.Split(':').LastOrDefault()?.Trim() ?? "Unknown";

                    // 如果序列号为空，尝试使用MAC地址
                    if (string.IsNullOrEmpty(info.SerialNumber))
                    {
                        var macAddress = deviceInfo.ToString()
                            ?.Split(',')
                            .FirstOrDefault(x => x.Contains("MAC"))?.Split(':').LastOrDefault()?.Trim();
                        info.SerialNumber = !string.IsNullOrEmpty(macAddress) ? macAddress : deviceInfo.chSerialNumber;
                        Log.Warning("相机序列号为空，使用MAC地址作为备选：{Mac}", macAddress);
                    }

                    info.MacAddress = deviceInfo.ToString()
                        ?.Split(',')
                        .FirstOrDefault(x => x.Contains("MAC"))?.Split(':').LastOrDefault()?.Trim() ??
                        deviceInfo.chSerialNumber;
                }
                else
                {
                    info.IpAddress = "USB";
                    info.MacAddress = deviceInfo.chSerialNumber;
                }

                // 记录详细的设备信息
                Log.Information("发现相机：{Model} (SN:{SerialNumber}, IP:{IP}, MAC:{MAC})",
                    info.Model, info.SerialNumber, info.IpAddress, info.MacAddress);

                cameras.Add(info);
            }

            Log.Information("发现 {Count} 台相机", cameras.Count);
            return cameras;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "枚举海康工业相机失败");
            return null;
        }
    }

    public void UpdateConfiguration(CameraSettings config)
    {
        ArgumentNullException.ThrowIfNull(config);
        
        LogAndReturn(() =>
        {
            _configuration = config;
            _deviceIdentifier = config.SelectedCameras.FirstOrDefault()?.SerialNumber;
            
            if (string.IsNullOrEmpty(_deviceIdentifier))
            {
                Log.Warning("未配置相机序列号");
                return false;
            }

            Log.Information("更新相机配置，目标序列号：{SerialNumber}", _deviceIdentifier);
            
            // 如果需要重新连接设备
            if (IsConnected && _device != null && ShouldReconnect(config))
            {
                return ReconnectDevice();
            }

            return true;
        }, "相机配置更新成功", "更新相机配置失败");
    }

    /// <summary>
    ///     启动图像处理线程
    /// </summary>
    private void StartProcessingThread()
    {
        _processingTask = Task.Run(async () =>
        {
            try
            {
                while (!_processingCancellation.Token.IsCancellationRequested)
                    try
                    {
                        // 从通道读取数据
                        var (imageData, imageSize) =
                            await _imageChannel.Reader.ReadAsync(_processingCancellation.Token);
                        await ProcessImageDataAsync(imageData, imageSize);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "处理图像数据时发生错误");
                    }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "图像处理线程异常退出");
            }
        }, _processingCancellation.Token);
    }

    /// <summary>
    ///     处理图像数据
    /// </summary>
    private async Task ProcessImageDataAsync(IntPtr pImageData, int nImageSize)
    {
        if (pImageData == IntPtr.Zero || nImageSize <= 0)
        {
            Log.Warning("收到无效的图像数据指针或大小");
            return;
        }

        Interlocked.Increment(ref _processingCount);

        try
        {
            await _processingSemaphore.WaitAsync();

            if (_device == null || !_isGrabbing)
            {
                Log.Warning("设备未就绪或未在采集状态，跳过处理");
                return;
            }

            var imageData = new byte[nImageSize];
            Marshal.Copy(pImageData, imageData, 0, nImageSize);

            using var image = CreateImageFromBuffer(imageData);

            var procParam = new MVIDCodeReader.MVID_PROC_PARAM
            {
                pImageBuf = pImageData,
                nImageLen = (uint)nImageSize,
                enImageType = MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_MONO8,
                nWidth = _imageWidth,
                nHeight = _imageHeight
            };

            var pProcParam = IntPtr.Zero;
            try
            {
                pProcParam = AllocateProcParam(procParam);

                var result = SafeSdkCall(() => _device!.MVID_CR_Process_NET(pProcParam,
                    MVIDCodeReader.MVID_BCR | MVIDCodeReader.MVID_TDCR));

                if (result == MVIDCodeReader.MVID_CR_OK)
                {
                    procParam = (MVIDCodeReader.MVID_PROC_PARAM)Marshal.PtrToStructure(pProcParam,
                        typeof(MVIDCodeReader.MVID_PROC_PARAM))!;

                    var barcodeLocations = ProcessBarcodeResults(procParam);

                    // 发布实时图像流
                    _realtimeImageSubject.OnNext((image.Clone(), barcodeLocations));

                    // 如果识别到条码，创建包裹信息
                    if (barcodeLocations.Count > 0)
                    {
                        var package = new PackageInfo
                        {
                            Barcode = barcodeLocations[0].Code,
                            CreateTime = DateTime.Now,
                            Image = image.Clone()
                        };
                        package.SetTriggerTimestamp(DateTime.Now);
                        _packageSubject.OnNext(package);
                    }
                }
            }
            finally
            {
                ReleaseProcParam(pProcParam);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理图像数据时发生错误");
        }
        finally
        {
            try
            {
                _processingSemaphore.Release();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放信号量时发生错误");
            }

            Interlocked.Decrement(ref _processingCount);
        }
    }

    /// <summary>
    ///     图像回调函数
    /// </summary>
    private void OnImageCallback(IntPtr pImageData, int nImageSize)
    {
        try
        {
            if (!ValidateImageData(pImageData, nImageSize))
            {
                return;
            }

            // 检查处理限制
            if (Interlocked.CompareExchange(ref _processingCount, 0, 0) >= MaxConcurrentProcessing)
            {
                Log.Debug("当前处理数量已达上限({Count})，跳过当前帧", _processingCount);
                return;
            }

            Log.Debug("收到图像数据，大小：{Size} 字节", nImageSize);

            // 将图像数据写入通道
            if (!_imageChannel.Writer.TryWrite((pImageData, nImageSize)))
            {
                Log.Warning("图像通道已满，丢弃当前帧");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "图像回调处理时发生错误");
        }
    }

    /// <summary>
    ///     验证图像数据
    /// </summary>
    private bool ValidateImageData(IntPtr pImageData, int nImageSize)
    {
        if (pImageData == IntPtr.Zero || nImageSize <= 0)
        {
            Log.Warning("收到无效的图像数据");
            return false;
        }

        if (_device != null && _isGrabbing) return true;
        Log.Debug("设备未就绪或未在采集状态，跳过回调处理");
        return false;

    }

    /// <summary>
    ///     注册回调函数
    /// </summary>
    private bool RegisterCallbacks()
    {
        try
        {
            if (_device == null)
            {
                Log.Error("设备未初始化，无法注册回调函数");
                return false;
            }

            // 使用锁保护SDK调用
            lock (_sdkLock)
            {
                // 注册图像回调
                var result = _device.MVID_CR_CAM_RegisterImageCallBack_NET(_imageCallback, IntPtr.Zero);
                if (result != MVIDCodeReader.MVID_CR_OK)
                {
                    Log.Error("注册图像回调失败：0x{Error:X}, {Message}", result, GetErrorMessage(result));
                    return false;
                }
            }

            Log.Information("回调函数注册成功");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "注册回调函数时发生错误");
            return false;
        }
    }

    /// <summary>
    ///     枚举相机设备
    /// </summary>
    public async Task<IEnumerable<DeviceCameraInfo>> EnumerateDevicesAsync()
    {
        try
        {
            Log.Information("正在枚举海康工业相机...");

            // 枚举设备
            var nRet = await Task.Run(() => MVIDCodeReader.MVID_CR_CAM_EnumDevices_NET(ref _deviceList));
            if (nRet != MVIDCodeReader.MVID_CR_OK)
            {
                Log.Error("枚举设备失败：{Error}", nRet);
                return [];
            }

            if (_deviceList.nDeviceNum == 0)
            {
                Log.Warning("未发现任何相机");
                return [];
            }

            // 转换为通用相机信息
            var cameras = new List<DeviceCameraInfo>();
            for (var i = 0; i < _deviceList.nDeviceNum; i++)
            {
                var deviceInfo = (MVIDCodeReader.MVID_CAMERA_INFO)Marshal.PtrToStructure(
                    _deviceList.pstCamInfo[i],
                    typeof(MVIDCodeReader.MVID_CAMERA_INFO))!;

                var info = new DeviceCameraInfo()
                {
                    SerialNumber = deviceInfo.chSerialNumber.Trim('\0'),
                    Model = deviceInfo.chModelName.Trim('\0')
                };

                if (deviceInfo.nCamType == MVIDCodeReader.MVID_GIGE_CAM)
                {
                    info.IpAddress = deviceInfo.ToString()
                        ?.Split(',')
                        .FirstOrDefault(x => x.Contains("IP"))?.Split(':').LastOrDefault()?.Trim() ?? "Unknown";

                    // 如果序列号为空，尝试使用MAC地址
                    if (string.IsNullOrEmpty(info.SerialNumber))
                    {
                        var macAddress = deviceInfo.ToString()
                            ?.Split(',')
                            .FirstOrDefault(x => x.Contains("MAC"))?.Split(':').LastOrDefault()?.Trim();
                        info.SerialNumber = !string.IsNullOrEmpty(macAddress) ? macAddress : deviceInfo.chSerialNumber;
                        Log.Warning("相机序列号为空，使用MAC地址作为备选：{Mac}", macAddress);
                    }

                    info.MacAddress = deviceInfo.ToString()
                                          ?.Split(',')
                                          .FirstOrDefault(x => x.Contains("MAC"))?.Split(':').LastOrDefault()?.Trim() ??
                                      deviceInfo.chSerialNumber;
                }
                else
                {
                    info.IpAddress = "USB";
                    info.MacAddress = deviceInfo.chSerialNumber;
                }

                // 记录详细的设备信息
                Log.Information("发现相机：{Model} (SN:{SerialNumber}, IP:{IP}, MAC:{MAC})",
                    info.Model, info.SerialNumber, info.IpAddress, info.MacAddress);

                cameras.Add(info);
            }

            Log.Information("发现 {Count} 台相机", cameras.Count);
            return cameras;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "枚举海康工业相机失败");
            throw;
        }
    }

    /// <summary>
    ///     获取图像参数
    /// </summary>
    private bool GetImageParameters()
    {
        try
        {
            if (_device == null)
            {
                Log.Error("设备未初始化，无法获取图像参数");
                return false;
            }

            // 获取图像宽度
            var widthValue = new MVIDCodeReader.MVID_CAM_INTVALUE_EX();
            var nRet = _device.MVID_CR_CAM_GetIntValue_NET("Width", ref widthValue);
            if (nRet != MVIDCodeReader.MVID_CR_OK)
            {
                Log.Error("获取图像宽度失败：0x{Error:X}, {Message}", nRet, GetErrorMessage(nRet));
                return false;
            }
            _imageWidth = (ushort)widthValue.nCurValue;

            // 获取图像高度
            var heightValue = new MVIDCodeReader.MVID_CAM_INTVALUE_EX();
            nRet = _device.MVID_CR_CAM_GetIntValue_NET("Height", ref heightValue);
            if (nRet != MVIDCodeReader.MVID_CR_OK)
            {
                Log.Error("获取图像高度失败：0x{Error:X}, {Message}", nRet, GetErrorMessage(nRet));
                return false;
            }
            _imageHeight = (ushort)heightValue.nCurValue;

            Log.Information("获取图像参数成功：宽度={Width}, 高度={Height}", _imageWidth, _imageHeight);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取图像参数时发生错误");
            return false;
        }
    }

    /// <summary>
    ///     连接设备的具体实现
    /// </summary>
    private async Task<bool> ConnectDeviceInternalAsync()
    {
        if (!ValidateConfiguration())
        {
            return false;
        }

        return await LogAndReturnAsync(async () =>
        {
            Log.Information("开始连接设备，目标序列号：{SerialNumber}", _deviceIdentifier);

            // 释放旧设备
            if (_device != null)
            {
                await ReleaseDeviceAsync();
            }

            // 创建新的设备实例
            _device = new MVIDCodeReader();

            // 枚举并查找目标设备
            var deviceIndex = await FindTargetDeviceAsync();
            if (deviceIndex < 0)
            {
                return false;
            }

            // 创建设备句柄
            if (!await CreateDeviceHandleAsync())
            {
                return false;
            }

            // 绑定设备
            if (!await BindDeviceAsync(deviceIndex))
            {
                return false;
            }

            // 获取图像参数
            if (!GetImageParameters())
            {
                return false;
            }

            // 注册回调函数
            if (!RegisterCallbacks())
            {
                return false;
            }

            IsConnected = true;
            ConnectionChanged?.Invoke(_deviceIdentifier!, true);
            return true;
        }, "相机连接成功", "连接海康工业相机失败");
    }

    /// <summary>
    ///     释放设备资源
    /// </summary>
    private async Task ReleaseDeviceAsync()
    {
        try
        {
            _device!.MVID_CR_DestroyHandle_NET();
            _device = null;
            await Task.Delay(100); // 等待资源释放
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "释放旧设备句柄时发生错误");
        }
    }

    /// <summary>
    ///     查找目标设备
    /// </summary>
    private async Task<int> FindTargetDeviceAsync()
    {
        var nRet = await Task.Run(() => MVIDCodeReader.MVID_CR_CAM_EnumDevices_NET(ref _deviceList));
        if (nRet != MVIDCodeReader.MVID_CR_OK)
        {
            Log.Error("枚举设备失败：{Error}", nRet);
            return -1;
        }

        if (_deviceList.nDeviceNum == 0)
        {
            Log.Error("未发现任何相机");
            return -1;
        }

        for (var i = 0; i < _deviceList.nDeviceNum; i++)
        {
            var deviceInfo = (MVIDCodeReader.MVID_CAMERA_INFO)Marshal.PtrToStructure(
                _deviceList.pstCamInfo[i],
                typeof(MVIDCodeReader.MVID_CAMERA_INFO))!;

            var currentSerialNumber = deviceInfo.chSerialNumber.Trim('\0');
            Log.Debug("比较序列号：当前={Current}, 目标={Target}", currentSerialNumber, _deviceIdentifier);

            if (!string.Equals(currentSerialNumber, _deviceIdentifier, StringComparison.OrdinalIgnoreCase)) continue;
            Log.Information("找到目标相机：{SerialNumber}", currentSerialNumber);
            return i;
        }

        Log.Error("未找到序列号为 {SerialNumber} 的相机", _deviceIdentifier);
        return -1;
    }

    /// <summary>
    ///     创建设备句柄
    /// </summary>
    private async Task<bool> CreateDeviceHandleAsync()
    {
        const int maxRetries = 3;
        for (var retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                var nRet = await Task.Run(() =>
                    _device!.MVID_CR_CreateHandle_NET(MVIDCodeReader.MVID_BCR | MVIDCodeReader.MVID_TDCR));
                
                if (nRet == MVIDCodeReader.MVID_CR_OK)
                {
                    Log.Information("创建设备句柄成功");
                    return true;
                }

                Log.Warning("创建设备句柄失败（第{Attempt}次尝试）：0x{Error:X8}, {Message}",
                    retry + 1, nRet, GetErrorMessage(nRet));

                if (retry >= maxRetries - 1) continue;
                await Task.Delay(1000);
                _device = new MVIDCodeReader();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "创建设备句柄时发生错误（第{Attempt}次尝试）", retry + 1);
                if (retry < maxRetries - 1)
                {
                    await Task.Delay(1000);
                    _device = new MVIDCodeReader();
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     绑定设备
    /// </summary>
    private async Task<bool> BindDeviceAsync(int deviceIndex)
    {
        if (deviceIndex < 0 || deviceIndex >= _deviceList.nDeviceNum)
        {
            Log.Error("设备索引无效：{Index}", deviceIndex);
            return false;
        }

        var nRet = await Task.Run(() => _device!.MVID_CR_CAM_BindDevice_NET(_deviceList.pstCamInfo[deviceIndex]));
        if (nRet == MVIDCodeReader.MVID_CR_OK) return true;
        Log.Error("绑定设备失败：0x{Error:X8}, {Message}", nRet, GetErrorMessage(nRet));
        return false;

    }

    /// <summary>
    ///     停止采集的具体实现
    /// </summary>
    private async Task StopGrabbingInternalAsync()
    {
        try
        {
            if (!_isGrabbing || _device == null) return;

            Log.Information("正在停止采集...");

            var result = await Task.Run(_device.MVID_CR_CAM_StopGrabbing_NET);
            if (result != MVIDCodeReader.MVID_CR_OK)
            {
                Log.Error("停止采集失败：0x{Error:X}, {Message}", result, GetErrorMessage(result));
                return;
            }

            _isGrabbing = false;
            Log.Information("停止采集成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止采集时发生错误");
        }
    }

    /// <summary>
    ///     获取错误码对应的错误信息
    /// </summary>
    private static string GetErrorMessage(int errorCode)
    {
        return errorCode switch
        {
            MVIDCodeReader.MVID_CR_OK => "成功",
            unchecked((int)0x80000000) => "错误或无效的句柄",
            unchecked((int)0x80000001) => "功能不支持",
            unchecked((int)0x80000002) => "缓冲区已满",
            unchecked((int)0x80000003) => "调用顺序错误",
            unchecked((int)0x80000004) => "参数错误",
            unchecked((int)0x80000005) => "申请资源失败",
            unchecked((int)0x80000006) => "无数据",
            unchecked((int)0x80000007) => "前置条件错误，或运行环境改变",
            unchecked((int)0x80000008) => "凭证错误，可能因为加密狗未安装或已过期",
            unchecked((int)0x8000000A) => "过滤规则错误",
            unchecked((int)0x8000000B) => "动态导入DLL文件失败",
            unchecked((int)0x80000012) => "JPG编码错误",
            unchecked((int)0x80000013) => "图像异常，可能由于丢包或图像格式、宽度、高度不正确",
            unchecked((int)0x80000014) => "格式转换错误",
            unchecked((int)0x800000FF) => "未知错误",
            unchecked((int)0x80000100) => "通用错误",
            unchecked((int)0x80000101) => "无效参数",
            unchecked((int)0x80000102) => "值超出范围",
            unchecked((int)0x80000103) => "属性错误",
            unchecked((int)0x80000104) => "运行环境错误",
            unchecked((int)0x80000105) => "逻辑错误",
            unchecked((int)0x80000106) => "节点访问条件错误",
            unchecked((int)0x80000107) => "超时",
            unchecked((int)0x80000108) => "转换异常",
            unchecked((int)0x800001FF) => "GeniCam未知错误",
            unchecked((int)0x80000200) => "设备不支持的命令",
            unchecked((int)0x80000201) => "目标地址不存在",
            unchecked((int)0x80000202) => "目标地址不可写",
            unchecked((int)0x80000203) => "无访问权限",
            unchecked((int)0x80000204) => "设备忙或网络断开",
            unchecked((int)0x80000205) => "网络包错误",
            unchecked((int)0x80000206) => "网络错误",
            unchecked((int)0x80000221) => "IP地址冲突",
            unchecked((int)0x80000300) => "USB读取错误",
            unchecked((int)0x80000301) => "USB写入错误",
            unchecked((int)0x80000302) => "设备异常",
            unchecked((int)0x80000303) => "GeniCam错误",
            unchecked((int)0x80000304) => "带宽不足",
            unchecked((int)0x80000305) => "驱动不匹配或未安装",
            unchecked((int)0x800003FF) => "USB未知错误",
            unchecked((int)0x80002100) => "相机错误",
            unchecked((int)0x80002200) => "一维码错误",
            unchecked((int)0x80002300) => "二维码错误",
            unchecked((int)0x80002400) => "图像裁剪错误",
            unchecked((int)0x80002500) => "脚本规则错误",
            _ => $"未知错误码：0x{errorCode:X8}"
        };
    }

    /// <summary>
    ///     检查是否需要重新连接设备
    /// </summary>
    private bool ShouldReconnect(CameraSettings newConfig)
    {
        var oldSerial = _configuration.SelectedCameras.FirstOrDefault()?.SerialNumber;
        var newSerial = newConfig.SelectedCameras.FirstOrDefault()?.SerialNumber;
        
        return !string.Equals(oldSerial, newSerial, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     重新连接设备
    /// </summary>
    private bool ReconnectDevice()
    {
        Stop();
        Thread.Sleep(100); // 等待设备完全停止
        return ConnectDeviceInternalAsync().Result;
    }

    /// <summary>
    ///     验证相机配置
    /// </summary>
    private bool ValidateConfiguration()
    {
        if (!_configuration.SelectedCameras.Any())
        {
            Log.Error("未选择任何相机");
            return false;
        }

        _deviceIdentifier = _configuration.SelectedCameras.FirstOrDefault()?.SerialNumber;
        if (!string.IsNullOrEmpty(_deviceIdentifier)) return true;
        Log.Error("相机序列号为空");
        return false;

    }

    public void Dispose()
    {
        try
        {
            Log.Information("正在释放海康工业相机资源...");

            // 1. 停止处理线程
            _processingCancellation.Cancel();
            try
            {
                _processingTask?.Wait(TimeSpan.FromSeconds(5));
                Log.Information("处理线程已停止");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "等待处理线程退出时发生错误");
            }

            // 2. 停止采集
            if (_device != null)
            {
                Stop();
                Thread.Sleep(500); // 等待采集完全停止

                try
                {
                    // 3. 销毁设备句柄
                    _device.MVID_CR_DestroyHandle_NET();
                    Log.Information("设备句柄已销毁");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "释放设备资源时发生错误");
                }
                finally
                {
                    _device = null;
                }
            }

            // 4. 释放回调函数句柄
            if (_imageCallbackHandle.IsAllocated)
            {
                _imageCallbackHandle.Free();
                Log.Information("图像回调句柄已释放");
            }

            // 5. 释放其他资源
            _processingCancellation.Dispose();
            _packageSubject.Dispose();
            _realtimeImageSubject.Dispose();
            _processingSemaphore.Dispose();
            Log.Information("其他资源已释放");

            // 6. 更新连接状态
            IsConnected = false;
            ConnectionChanged?.Invoke(_deviceIdentifier ?? string.Empty, false);

            Log.Information("海康工业相机资源已释放完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放海康工业相机资源时发生错误");
            throw;
        }
        finally
        {
            GC.SuppressFinalize(this);
        }
    }

    #region 工具方法

    /// <summary>
    ///     从图像缓冲区创建图像
    /// </summary>
    private Image<Rgba32> CreateImageFromBuffer(byte[] imageData)
    {
        var image = new Image<Rgba32>(_imageWidth, _imageHeight);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < _imageHeight; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < _imageWidth; x++)
                {
                    var gray = imageData[y * _imageWidth + x];
                    row[x] = new Rgba32(gray, gray, gray, 255);
                }
            }
        });
        return image;
    }

    /// <summary>
    ///     安全的SDK调用包装器
    /// </summary>
    private TResult SafeSdkCall<TResult>(Func<TResult> action, [CallerMemberName] string caller = "")
    {
        lock (_sdkLock)
        {
            if (_device == null)
            {
                Log.Warning($"设备未初始化，无法执行 {caller}");
                return default!;
            }

            try
            {
                return action();
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"SDK调用失败：{caller}");
                return default!;
            }
        }
    }

    /// <summary>
    ///     处理条码识别结果
    /// </summary>
    private static List<BarcodeLocation> ProcessBarcodeResults(MVIDCodeReader.MVID_PROC_PARAM procParam)
    {
        var barcodes = new List<BarcodeLocation>();
        if (procParam.stCodeList.nCodeNum <= 0) return barcodes;
        for (var i = 0; i < procParam.stCodeList.nCodeNum; i++)
        {
            var codeInfo = procParam.stCodeList.stCodeInfo[i];
            var corners = new Point[4];
            for (var j = 0; j < 4; j++)
                corners[j] = new Point(codeInfo.stCornerPt[j].nX, codeInfo.stCornerPt[j].nY);

            var barcodeLocation = new BarcodeLocation(corners.ToList())
            {
                Code = codeInfo.strCode.TrimEnd('\0'),
                Type = codeInfo.enBarType.ToString()
            };
            barcodes.Add(barcodeLocation);
        }
        return barcodes;
    }

    /// <summary>
    ///     分配处理参数内存
    /// </summary>
    private static IntPtr AllocateProcParam(MVIDCodeReader.MVID_PROC_PARAM procParam)
    {
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(procParam));
        Marshal.StructureToPtr(procParam, ptr, false);
        return ptr;
    }

    /// <summary>
    ///     释放处理参数内存
    /// </summary>
    private static void ReleaseProcParam(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    ///     统一的错误处理模式
    /// </summary>
    private static void LogAndReturn(Func<bool> action, string successMsg, string errorMsg)
    {
        try
        {
            var result = action();
            if (result)
            {
                Log.Information(successMsg);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, errorMsg);
        }
    }

    /// <summary>
    ///     统一的异步错误处理模式
    /// </summary>
    private static async Task<bool> LogAndReturnAsync(Func<Task<bool>> action, string successMsg, string errorMsg)
    {
        try
        {
            var result = await action();
            if (result)
            {
                Log.Information(successMsg);
            }
            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, errorMsg);
            return false;
        }
    }

    #endregion
}
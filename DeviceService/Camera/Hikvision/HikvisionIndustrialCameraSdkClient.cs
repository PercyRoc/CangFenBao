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
using System.Collections.Concurrent;

namespace DeviceService.Camera.Hikvision;

/// <summary>
///     海康工业相机SDK客户端
/// </summary>
public class HikvisionIndustrialCameraSdkClient : ICameraService
{
    // 根据CPU核心数调整并发处理数量
    private readonly int _maxConcurrentProcessing = Math.Max(2, Environment.ProcessorCount / 2);

    // 保持委托的引用，防止被GC回收
    private readonly MVIDCodeReader.cbOutputdelegate _imageCallback;
    private readonly Channel<(IntPtr imageData, int imageSize)> _imageChannel;

    private readonly Subject<PackageInfo> _packageSubject = new();
    private readonly CancellationTokenSource _processingCancellation = new();
    private readonly SemaphoreSlim _processingSemaphore;

    private readonly Subject<(Image<Rgba32> image, IReadOnlyList<BarcodeLocation> barcodes)>
        _realtimeImageSubject = new();

    // 添加图像对象池
    private readonly ConcurrentQueue<Image<Rgba32>> _imagePool = new();
    private const int MaxImagePoolSize = 10;

    // 添加最新图像缓存
    private Image<Rgba32>? _latestImage;
    private readonly object _latestImageLock = new();

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
        _processingSemaphore = new SemaphoreSlim(_maxConcurrentProcessing);
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

            // 2. 检查设备状态
            if (_device == null)
            {
                Log.Warning("设备未初始化，尝试重新初始化...");
                ConnectDeviceInternalAsync().GetAwaiter().GetResult();
            }

            // 3. 再次检查设备是否已经初始化
            if (_device == null)
            {
                Log.Error("设备初始化失败，无法开始采集");
                return false;
            }

            // 4. 检查设备连接状态
            if (!IsConnected)
            {
                Log.Warning("设备未连接，尝试重新连接...");
                ConnectDeviceInternalAsync().GetAwaiter().GetResult();
                
                if (!IsConnected)
                {
                    Log.Error("设备连接失败，无法开始采集");
                    return false;
                }
            }

            // 5. 尝试开始采集，最多重试3次
            const int maxRetries = 3;
            for (var i = 1; i <= maxRetries; i++)
            {
                try
                {
                    // 在每次尝试前检查设备状态
                    if (_device == null)
                    {
                        Log.Error("设备句柄无效，重新初始化设备");
                        ConnectDeviceInternalAsync().GetAwaiter().GetResult();
                        if (_device == null) continue;
                    }

                    var result = _device.MVID_CR_CAM_StartGrabbing_NET();
                    if (result == MVIDCodeReader.MVID_CR_OK)
                    {
                        _isGrabbing = true;
                        Log.Information("开始采集成功");
                        return true;
                    }

                    Log.Warning("开始采集失败（第{Attempt}次尝试）：0x{Result:X}, {Message}",
                        i, result, GetErrorMessage(result));

                    // 如果是调用顺序错误或句柄无效，尝试重新初始化
                    if (result == unchecked((int)0x80000003) || // MVID_CR_E_CALLORDER
                        result == unchecked((int)0x80000000))   // MVID_CR_INVALID_HANDLE
                    {
                        Log.Information("检测到调用顺序错误或句柄无效，尝试重新初始化...");
                        StopGrabbingInternalAsync().GetAwaiter().GetResult();
                        Thread.Sleep(500); // 等待设备状态稳定
                        ConnectDeviceInternalAsync().GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "开始采集时发生异常（第{Attempt}次尝试）", i);
                    if (i == maxRetries) throw;
                }

                // 在重试之前等待一段时间
                if (i < maxRetries)
                {
                    Thread.Sleep(1000 * i); // 递增等待时间
                }
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
    /// 获取最新的图像
    /// </summary>
    /// <returns>成功返回图像数据，失败返回 null</returns>
    public Image<Rgba32>? ExecuteSoftTrigger()
    {
        try
        {
            lock (_latestImageLock)
            {
                if (_latestImage == null)
                {
                    Log.Warning("没有可用的图像");
                    return null;
                }

                var image = _latestImage.Clone();
                Log.Information("成功获取最新图像");
                return image;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取最新图像时发生错误");
            return null;
        }
    }

    /// <summary>
    /// 异步停止相机采集并释放资源
    /// </summary>
    /// <param name="timeoutMs">超时时间（毫秒）</param>
    /// <returns>操作是否成功</returns>
    public Task<bool> StopAsync(int timeoutMs = 3000)
    {
        try
        {
            Log.Information("正在停止采集并释放资源...");
            
            // 检查设备是否已连接
            if (!IsConnected || _device == null)
            {
                Log.Debug("设备未连接，无需停止");
                return Task.FromResult(true);
            }

            // 首先停止图像处理线程和释放通道，避免资源竞争
            try
            {
                // 1. 先完成图像通道写入，阻止新的图像处理请求
                _imageChannel.Writer.TryComplete();
                Log.Debug("图像通道已完成，不再接收新图像");
                
                // 2. 发送停止信号，确保处理线程能够安全退出
                _processingCancellation.Cancel();
                Log.Debug("已发送处理取消信号");
                
                // 3. 等待一段时间，确保不再有线程访问信号量
                Thread.Sleep(100);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "停止图像处理线程时发生错误");
            }

            // 保存设备引用并重置状态，防止其他线程访问
            var oldDevice = _device;
            _device = null;
            IsConnected = false;
            
            bool success;
            try
            {
                // 1. 停止采集
                if (_isGrabbing)
                {
                    try
                    {
                        // 使用同步方式停止采集
                        lock (_sdkLock)
                        {
                            var result = oldDevice.MVID_CR_CAM_StopGrabbing_NET();
                            if (result != MVIDCodeReader.MVID_CR_OK)
                            {
                                Log.Warning("停止采集返回错误: 0x{Error:X}, {Message}", 
                                    result, GetErrorMessage(result));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "停止采集时发生错误");
                    }
                    finally
                    {
                        _isGrabbing = false;
                    }
                }
                
                // 2. 销毁设备句柄
                try
                {
                    Log.Debug("正在销毁设备句柄...");
                    var result = oldDevice.MVID_CR_DestroyHandle_NET();
                    if (result != MVIDCodeReader.MVID_CR_OK && 
                        result != unchecked((int)0x80000000)) // 忽略句柄无效错误
                    {
                        Log.Warning("销毁设备句柄返回错误: 0x{Error:X}, {Message}",
                            result, GetErrorMessage(result));
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "销毁设备句柄时发生错误");
                }
                
                Log.Information("设备资源已释放");
                success = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "停止过程中发生错误");
                success = false;
            }
            finally
            {
                // 确保状态被重置
                _device = null;
                IsConnected = false;
                _isGrabbing = false;
                
                // 触发连接状态改变事件
                try
                {
                    ConnectionChanged?.Invoke(_deviceIdentifier ?? string.Empty, false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "触发连接状态改变事件时发生错误");
                }
            }
            
            return Task.FromResult(success);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行停止时发生错误");
            // 确保设备实例被清空
            _device = null;
            IsConnected = false;
            _isGrabbing = false;
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// 停止相机采集 (内部实现)
    /// </summary>
    private async Task StopGrabbingInternalAsync()
    {
        try
        {
            if (!_isGrabbing || _device == null) return;

            Log.Information("正在停止采集...");

            var result = await Task.Run(() => _device.MVID_CR_CAM_StopGrabbing_NET());
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
    /// 获取错误码对应的错误信息
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

    public IEnumerable<DeviceCameraInfo>? GetCameraInfos()
    {
        try
        {
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
                    catch (ChannelClosedException)
                    {
                        // 通道被关闭，这是正常的退出信号，不需要报错
                        Log.Information("图像通道已关闭，处理线程准备退出");
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
            finally
            {
                Log.Debug("图像处理线程已退出");
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
            // 安全获取信号量，检查是否已被释放
            try
            {
                var semaphore = _processingSemaphore;
                if (_processingCancellation.IsCancellationRequested)
                {
                    Log.Debug("信号量已释放或处理已取消，跳过处理");
                    return;
                }
                
                await semaphore.WaitAsync(_processingCancellation.Token);
            }
            catch (ObjectDisposedException)
            {
                Log.Debug("信号量已被释放，跳过处理");
                return;
            }
            catch (OperationCanceledException)
            {
                Log.Debug("处理已被取消，跳过处理");
                return;
            }

            if (_device == null || !_isGrabbing)
            {
                Log.Warning("设备未就绪或未在采集状态，跳过处理");
                return;
            }

            var imageData = new byte[nImageSize];
            Marshal.Copy(pImageData, imageData, 0, nImageSize);

            // 创建一个图像用于所有操作
            var image = CreateImageFromBuffer(imageData);
            
            try
            {
                // 更新最新图像缓存
                lock (_latestImageLock)
                {
                    _latestImage?.Dispose();
                    _latestImage = image.Clone(); // 这里必须克隆，因为image会被后续处理
                }

                // 使用原始数据处理图像
                var procParam = new MVIDCodeReader.MVID_PROC_PARAM
                {
                    pImageBuf = pImageData,
                    nImageLen = (uint)nImageSize,
                    enImageType = MVIDCodeReader.MVID_IMAGE_TYPE.MVID_IMAGE_MONO8,
                    nWidth = _imageWidth,
                    nHeight = _imageHeight
                };
                
                // 处理图像
                ProcessWithParameters(image, procParam);
            }
            catch
            {
                // 确保图像资源被释放
                image.Dispose();
                throw;
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
                // 安全释放信号量
                SemaphoreSlim semaphore = _processingSemaphore;
                if (!_processingCancellation.IsCancellationRequested)
                {
                    try
                    {
                        semaphore.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                        // 忽略已释放的信号量异常
                        Log.Debug("尝试释放已处置的信号量");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放信号量时发生错误");
            }

            Interlocked.Decrement(ref _processingCount);
        }
    }

    /// <summary>
    /// 使用指定参数处理图像
    /// </summary>
    private void ProcessWithParameters(Image<Rgba32> image, MVIDCodeReader.MVID_PROC_PARAM procParam)
    {
        var pProcParam = IntPtr.Zero;
        try
        {
            pProcParam = AllocateProcParam(procParam);

            var result = SafeSdkCall(() => _device!.MVID_CR_Process_NET(pProcParam,
                MVIDCodeReader.MVID_BCR | MVIDCodeReader.MVID_TDCR));

            if (result != MVIDCodeReader.MVID_CR_OK) return;
            procParam = (MVIDCodeReader.MVID_PROC_PARAM)Marshal.PtrToStructure(pProcParam,
                typeof(MVIDCodeReader.MVID_PROC_PARAM))!;

            var barcodeLocations = ProcessBarcodeResults(procParam);

            // 发布实时图像流 - 使用引用而不是克隆
            _realtimeImageSubject.OnNext((image, barcodeLocations));

            // 如果识别到条码，创建包裹信息
            if (barcodeLocations.Count <= 0) return;
            var package = new PackageInfo
            {
                Barcode = barcodeLocations[0].Code,
                CreateTime = DateTime.Now,
                Image = image // 直接使用图像，不再克隆
            };
            package.SetTriggerTimestamp(DateTime.Now);
            _packageSubject.OnNext(package);
        }
        finally
        {
            ReleaseProcParam(pProcParam);
            // 如果没有被发送到subject，则需要返回到对象池或释放
            ReturnImageToPool(image);
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
            if (Interlocked.CompareExchange(ref _processingCount, 0, 0) >= _maxConcurrentProcessing)
            {
                Log.Debug("当前处理数量已达上限({Count})，跳过当前帧", _processingCount);
                return;
            }

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
            // 2. 创建新的设备实例
            _device = new MVIDCodeReader();

            // 3. 枚举并查找目标设备
            var deviceIndex = await FindTargetDeviceAsync();
            if (deviceIndex < 0)
            {
                Log.Error("未找到目标设备");
                return false;
            }

            // 4. 创建设备句柄
            if (!await CreateDeviceHandleAsync())
            {
                Log.Error("创建设备句柄失败");
                return false;
            }

            // 5. 绑定设备
            if (!await BindDeviceAsync(deviceIndex))
            {
                Log.Error("绑定设备失败");
                return false;
            }

            // 6. 获取图像参数
            if (!GetImageParameters())
            {
                Log.Error("获取图像参数失败");
                return false;
            }

            // 7. 注册回调函数
            if (!RegisterCallbacks())
            {
                Log.Error("注册回调函数失败");
                return false;
            }

            IsConnected = true;
            ConnectionChanged?.Invoke(_deviceIdentifier!, true);
            return true;
        }, "相机连接成功", "连接海康工业相机失败");
    }

    /// <summary>
    ///     查找目标设备
    /// </summary>
    private async Task<int> FindTargetDeviceAsync()
    {
        Log.Debug("开始枚举设备...");
        var nRet = await Task.Run(() => MVIDCodeReader.MVID_CR_CAM_EnumDevices_NET(ref _deviceList));
        if (nRet != MVIDCodeReader.MVID_CR_OK)
        {
            Log.Error("枚举设备失败：{Error}, {Message}", nRet, GetErrorMessage(nRet));
            return -1;
        }
    
        if (_deviceList.nDeviceNum == 0)
        {
            Log.Error("未发现任何相机");
            return -1;
                
        }

        Log.Debug("发现 {Count} 台相机，开始查找目标设备...", _deviceList.nDeviceNum);

        for (var i = 0; i < _deviceList.nDeviceNum; i++)
        {
            var deviceInfo = (MVIDCodeReader.MVID_CAMERA_INFO)Marshal.PtrToStructure(
                _deviceList.pstCamInfo[i],
                typeof(MVIDCodeReader.MVID_CAMERA_INFO))!;

            var currentSerialNumber = deviceInfo.chSerialNumber.Trim('\0');
            Log.Debug("检查设备 {Index}: 序列号={Current}, 目标={Target}", i, currentSerialNumber, _deviceIdentifier);

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
                Log.Debug("正在创建设备句柄（第{Attempt}次尝试）...", retry + 1);
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
                Log.Debug("等待1秒后重试...");
                await Task.Delay(1000);
                _device = new MVIDCodeReader();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "创建设备句柄时发生错误（第{Attempt}次尝试）", retry + 1);
                if (retry < maxRetries - 1)
                {
                    Log.Debug("等待1秒后重试...");
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

        Log.Debug("正在绑定设备（索引：{Index}）...", deviceIndex);
        var nRet = await Task.Run(() => _device!.MVID_CR_CAM_BindDevice_NET(_deviceList.pstCamInfo[deviceIndex]));
        if (nRet == MVIDCodeReader.MVID_CR_OK)
        {
            Log.Information("设备绑定成功");
            return true;
        }
        
        Log.Error("绑定设备失败：0x{Error:X8}, {Message}", nRet, GetErrorMessage(nRet));
        return false;
    }

    /// <summary>
    ///     验证相机配置
    /// </summary>
    private bool ValidateConfiguration()
    {
        if (_configuration.SelectedCameras.Count == 0)
        {
            Log.Error("未选择任何相机");
            return false;
        }

        _deviceIdentifier = _configuration.SelectedCameras.FirstOrDefault()?.SerialNumber;
        if (!string.IsNullOrEmpty(_deviceIdentifier)) return true;
        Log.Error("相机序列号为空");
        return false;

    }

    /// <summary>
    /// 异步释放资源
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            Log.Information("正在异步释放海康工业相机资源...");

            // 设置总超时保护
            using var overallTimeoutCts = new CancellationTokenSource(15000); // 15秒总超时

            // 1. 先停止相机，防止新的图像进入处理队列
            try
            {
                if (_device != null || IsConnected)
                {
                    await StopAsync(5000);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "在资源释放过程中停止设备时发生错误");
            }

            // 2. 安全停止图像处理线程
            try
            {
                // 确保图像通道已关闭
                if (!_imageChannel.Writer.TryComplete())
                {
                    Log.Debug("图像通道已经被关闭");
                }

                // 发送取消信号
                if (!_processingCancellation.IsCancellationRequested)
                {
                    _processingCancellation.Cancel();
                    Log.Debug("已发送处理取消信号");
                }
                
                // 等待处理线程完成 - 使用短暂的超时，避免卡死
                if (_processingTask is { IsCompleted: false })
                {
                    try
                    {
                        if (_processingTask.Wait(2000))
                        {
                            Log.Debug("处理线程已正常完成");
                        }
                        else
                        {
                            Log.Warning("等待处理线程完成超时");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "等待处理线程时发生错误");
                    }
                }
                
                // 设置为null，不再引用
                _processingTask = null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "停止图像处理线程时发生错误");
            }

            // 3. 释放最新图像缓存和图像对象池
            try 
            {
                Log.Debug("正在释放图像资源...");
                
                // 释放最新图像
                lock (_latestImageLock)
                {
                    _latestImage?.Dispose();
                    _latestImage = null;
                }
                
                // 清空图像对象池
                int count = 0;
                while (_imagePool.TryDequeue(out var pooledImage))
                {
                    pooledImage.Dispose();
                    count++;
                }
                Log.Debug("已释放{Count}个图像对象", count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放图像资源时出错");
            }

            // 4. 释放其他资源
            try
            {
                // 释放资源的顺序很重要，确保不会出现未引用的对象
                
                // 1. 释放回调句柄
                try
                {
                    if (_imageCallbackHandle.IsAllocated)
                    {
                        _imageCallbackHandle.Free();
                        Log.Debug("图像回调句柄已释放");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "释放图像回调句柄时出错");
                }
                
                // 2. 释放被订阅的对象
                try
                {
                    _packageSubject.Dispose();
                    _realtimeImageSubject.Dispose();
                    Log.Debug("事件流对象已释放");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "释放事件流对象时出错");
                }
                
                // 3. 最后释放可能被线程使用的对象
                try
                {
                    _processingCancellation.Dispose();
                    Thread.Sleep(50); // 短暂等待，确保没有线程正在使用信号量
                    _processingSemaphore.Dispose();
                    Log.Debug("线程同步对象已释放");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "释放线程同步对象时出错");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放资源时发生错误");
            }
            
            Log.Information("海康工业相机资源异步释放过程已完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "异步释放海康工业相机资源时发生错误");
        }
        finally
        {
            // 确保终结器不再运行
            GC.SuppressFinalize(this);
        }
    }

    #region 工具方法

    /// <summary>
    ///     从图像缓冲区创建图像
    /// </summary>
    private Image<Rgba32> CreateImageFromBuffer(byte[] imageData)
    {
        var image = GetImageFromPool();
        
        // 确定图像是灰度还是彩色
        var isGrayscale = imageData.Length <= _imageWidth * _imageHeight;
        
        image.ProcessPixelRows(accessor =>
        {
            if (isGrayscale)
            {
                // 灰度图像处理 - 使用批量处理提高性能
                for (var y = 0; y < _imageHeight; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    var rowOffset = y * _imageWidth;
                    
                    for (var x = 0; x < _imageWidth; x++)
                    {
                        var gray = imageData[rowOffset + x];
                        row[x] = new Rgba32(gray, gray, gray, 255);
                    }
                }
            }
            else
            {
                // RGB图像处理
                for (var y = 0; y < _imageHeight; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    var rowOffset = y * _imageWidth * 3;
                    
                    for (var x = 0; x < _imageWidth; x++)
                    {
                        var pixelOffset = rowOffset + x * 3;
                        if (pixelOffset + 2 < imageData.Length)
                        {
                            row[x] = new Rgba32(
                                imageData[pixelOffset],      // R
                                imageData[pixelOffset + 1],  // G
                                imageData[pixelOffset + 2],  // B
                                255);                      // A
                        }
                    }
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

    // 从对象池获取图像对象
    private Image<Rgba32> GetImageFromPool()
    {
        return _imagePool.TryDequeue(out var image) ? image : new Image<Rgba32>(_imageWidth, _imageHeight);
    }

    // 将图像对象返回池中
    private void ReturnImageToPool(Image<Rgba32> image)
    {
        if (_imagePool.Count < MaxImagePoolSize)
        {
            _imagePool.Enqueue(image);
        }
        else
        {
            image.Dispose();
        }
    }

    #endregion
}
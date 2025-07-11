using System.Windows.Media.Imaging;
using Common.Models.Package;
using DeviceService.DataSourceDevices.Camera.HuaRay;
using DeviceService.DataSourceDevices.Camera.Models;
using Serilog;
using Serilog.Events;
using SortingServices.Car.Service;

namespace CangFenBao.SDK
{
    /// <summary>
    /// 主SDK类，用于控制华睿相机和小车分拣系统。
    /// </summary>
    public class SortingSystemSdk : IAsyncDisposable
    {
        private readonly SdkConfig _config;
        private HuaRayCameraService? _cameraService;
        private CarSortingService? _carSortingServiceInternal;
        private CarSortService? _carSortService;
        private IDisposable? _packageSubscription;
        private IDisposable? _imageSubscription;
        private SdkImageService? _imageService;
        private HttpUploadService? _httpUploadService;

        /// <summary>
        /// 当相机识别到包裹并提取出完整信息后触发。
        /// 此时包裹的 ChuteNumber (格口号) 尚未设置。
        /// 用户需要在此事件中根据业务逻辑（如查询API）为包设置 ChuteNumber，
        /// 然后调用 SortPackageAsync 方法。
        /// </summary>
        public event EventHandler<PackageInfo>? PackageReady;

        /// <summary>
        /// 当包裹数据上传完成后触发，无论成功或失败。
        /// </summary>
        public event EventHandler<(PackageInfo package, UploadResponse? response)>? UploadCompleted;

        /// <summary>
        /// 当包裹因重量低于阈值而被丢弃时触发。
        /// </summary>
        public event EventHandler<PackageInfo>? PackageDiscarded;

        /// <summary>
        /// 当相机捕获到图像时触发。
        /// </summary>
        public event EventHandler<(BitmapSource Image, string CameraId)>? ImageReceived;

        /// <summary>
        /// 相机连接状态发生变化时触发。
        /// </summary>
        public event Action<string, bool>? CameraConnectionChanged;

        /// <summary>
        /// 分拣机（小车串口）连接状态发生变化时触发。
        /// </summary>
        public event Action<bool>? SorterConnectionChanged;

        /// <summary>
        /// 获取SDK是否正在运行。
        /// </summary>
        public bool IsRunning { get; private set; }

        /// <summary>
        /// 初始化分拣系统SDK。
        /// </summary>
        /// <param name="config">SDK配置参数</param>
        public SortingSystemSdk(SdkConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// 异步初始化所有服务和依赖项。
        /// 必须在调用 StartAsync 之前成功完成。
        /// </summary>
        /// <returns>如果初始化成功，则为 true；否则为 false。</returns>
        public async Task<bool> InitializeAsync()
        {
            try
            {
                // 配置 Serilog 日志
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                    .Enrich.FromLogContext()
                    .WriteTo.File(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "log-.txt"), 
                        rollingInterval: RollingInterval.Day,
                        fileSizeLimitBytes: 10 * 1024 * 1024, // 10MB
                        rollOnFileSizeLimit: true,
                        retainedFileCountLimit: 30,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .CreateLogger();

                Log.Information("SDK 初始化开始...");

                // 1. 创建服务实例
                var settingsService = new JsonSettingsService(_config);
                _imageService = new SdkImageService(_config);
                _httpUploadService = new HttpUploadService(_config);

                // 2. 初始化相机服务
                _cameraService = new HuaRayCameraService();

                // 3. 初始化小车服务
                _carSortingServiceInternal = new CarSortingService(settingsService);
                _carSortService = new CarSortService(_carSortingServiceInternal, settingsService);

                var carInitSuccess = await _carSortService.InitializeAsync();
                if (!carInitSuccess)
                {
                    Log.Error("小车服务初始化失败。");
                    return false;
                }

                // 4. 订阅内部事件并暴露为公共事件
                _packageSubscription = _cameraService.PackageStream.Subscribe(OnPackageReceived);
                _imageSubscription = _cameraService.ImageStreamWithId.Subscribe(OnImageReceived);
                _cameraService.ConnectionChanged += (id, status) =>
                {
                    Log.Information("相机 '{CameraId}' 连接状态: {Status}", id, status ? "在线" : "离线");
                    CameraConnectionChanged?.Invoke(id, status);
                };

                if (_carSortingServiceInternal != null)
                {
                    _carSortingServiceInternal.ConnectionChanged += (status) =>
                    {
                        Log.Information("分拣机串口连接状态: {Status}", status ? "已连接" : "已断开");
                        SorterConnectionChanged?.Invoke(status);
                    };
                }
                Log.Information("SDK 初始化成功！");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "SDK 初始化失败。请检查配置文件和设备连接。");
                return false;
            }
        }

        /// <summary>
        /// 启动所有服务，开始接收相机数据和处理分拣。
        /// </summary>
        public async Task StartAsync()
        {
            if (IsRunning) return;
            Log.Information("SDK 服务启动中...");

            // 启动相机
            _cameraService?.Start(_config.HuaRayConfigPath);

            if (_carSortService != null)
            {
                await _carSortService.StartAsync();
            }

            IsRunning = true;
            Log.Information("SDK 服务已启动，正在等待包裹数据...");
        }

        /// <summary>
        /// 停止所有服务。
        /// </summary>
        public async Task StopAsync()
        {
            if (!IsRunning) return;
            Log.Information("SDK 服务停止中...");

            _cameraService?.Stop();
            if (_carSortService != null)
            {
                await _carSortService.StopAsync();
            }

            IsRunning = false;
            Log.Information("SDK 服务已停止。");
        }

        /// <summary>
        /// 发送分拣指令。
        /// 用户应在 PackageReady 事件中获得 PackageInfo，设置 ChuteNumber，然后调用此方法。
        /// </summary>
        /// <param name="package">包含目标格口号的包裹信息。</param>
        public async Task<bool> SortPackageAsync(PackageInfo package)
        {
            if (!IsRunning || _carSortService == null)
            {
                Log.Warning("SDK 未运行或小车分拣服务未初始化，无法处理分拣指令。");
                return false;
            }
            Log.Information("开始处理包裹 {Barcode} 的分拣指令，目标格口: {ChuteNumber}", package.Barcode, package.ChuteNumber);
            var success = await _carSortService.ProcessPackageSortingAsync(package);
            if (success)
            {
                Log.Information("包裹 {Barcode} 的分拣指令已成功发送到队列。", package.Barcode);
            }
            else
            {
                Log.Error("包裹 {Barcode} 的分拣指令发送失败。", package.Barcode);
            }
            return success;
        }

        /// <summary>
        /// 直接向指定格口发送分拣指令。
        /// </summary>
        /// <param name="chuteNumber">目标格口号。</param>
        public async Task<bool> SortToChuteAsync(int chuteNumber)
        {
            if (!IsRunning || _carSortingServiceInternal == null)
            {
                Log.Warning("SDK 未运行或小车内部服务未初始化，无法直接发送格口指令。");
                return false;
            }
            Log.Information("直接向格口 {ChuteNumber} 发送分拣指令。", chuteNumber);
            var success = await _carSortingServiceInternal.SendCommandForChuteAsync(chuteNumber);
            if (success)
            {
                Log.Information("直接向格口 {ChuteNumber} 发送指令成功。", chuteNumber);
            }
            else
            {
                Log.Error("直接向格口 {ChuteNumber} 发送指令失败。", chuteNumber);
            }
            return success;
        }

        /// <summary>
        /// 获取可用的相机列表。
        /// </summary>
        public IEnumerable<CameraBasicInfo> GetAvailableCameras()
        {
            var cameras = _cameraService?.GetAvailableCameras() ?? [];
            var cameraBasicInfos = cameras as CameraBasicInfo[] ?? cameras.ToArray();
            Log.Debug("获取到 {Count} 个可用相机。", cameraBasicInfos.Length);
            return cameraBasicInfos;
        }

        private void OnPackageReceived(PackageInfo package)
        {
            Log.Information("收到原始包裹数据: 条码 {Barcode}, 重量 {Weight}g", package.Barcode, package.Weight);
            // 重量检查
            if (_config.MinimumWeightGrams > 0 && package.Weight < _config.MinimumWeightGrams)
            {
                PackageDiscarded?.Invoke(this, package);
                Log.Information("包裹 {Barcode} 因重量 ({Weight}g) 低于阈值 ({Threshold}g) 被丢弃", 
                    package.Barcode, package.Weight, _config.MinimumWeightGrams);
                return; 
            }

            Log.Information("包裹 {Barcode} 通过重量检查。", package.Barcode);
            // 触发包裹就绪事件，此时包裹可用于显示或初步记录
            PackageReady?.Invoke(this, package);
            
            // 如果启用了上传功能，则执行上传和自动分拣
            if (_config.EnableUpload)
            {
                Log.Information("启用上传功能，开始异步上传包裹 {Barcode}...", package.Barcode);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var response = await _httpUploadService!.UploadPackageAsync(package);
                        
                        UploadCompleted?.Invoke(this, (package, response));

                        if (response is { Code: 0, Chute: > 0 })
                        {
                            package.SetChuteNumber(response.Chute);
                            Log.Information("收到服务器分拣指令: 包裹 {Barcode} -> 格口 {Chute}", package.Barcode, package.ChuteNumber);
                            await SortPackageAsync(package);
                        }
                        else
                        {
                            Log.Warning("包裹 {Barcode} 上传失败或服务器未返回有效格口。服务器消息: {Message}. 响应码: {Code}", 
                                package.Barcode, response?.Message ?? "无响应", response?.Code ?? -1);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "异步上传包裹 {Barcode} 时发生错误。", package.Barcode);
                        UploadCompleted?.Invoke(this, (package, null)); // 上传失败也触发事件，response为null
                    }
                });
            }
            else
            {
                Log.Information("未启用上传功能，包裹 {Barcode} 等待手动分拣或处理。", package.Barcode);
            }
        }

        private void OnImageReceived((BitmapSource Image, string CameraId) data)
        {
            Log.Debug("收到相机 {CameraId} 的图像，尺寸: {Width}x{Height}", data.CameraId, data.Image.PixelWidth, data.Image.PixelHeight);
            
            // 如果启用了图像保存功能，使用图像服务处理图像
            if (_config.SaveImages && _imageService != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 创建一个带有图像的包裹信息用于图像处理
                        var packageForImage = PackageInfo.Create();
                        packageForImage.Image = data.Image;
                        await _imageService.ProcessAndSaveImageAsync(packageForImage);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "处理相机 {CameraId} 的图像时发生错误", data.CameraId);
                    }
                });
            }
            
            ImageReceived?.Invoke(this, data);
        }

        public async ValueTask DisposeAsync()
        {
            Log.Information("SDK 正在释放资源...");
            await StopAsync();
            _packageSubscription?.Dispose();
            _imageSubscription?.Dispose();
            _cameraService?.Dispose();
            _carSortService?.Dispose();
            if (_carSortingServiceInternal != null)
            {
                await _carSortingServiceInternal.DisposeAsync();
            }
            await Log.CloseAndFlushAsync(); // 确保所有日志都写入完毕
            GC.SuppressFinalize(this);
            Log.Information("SDK 资源释放完毕。");
        }
    }
}
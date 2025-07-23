using System.Windows.Media.Imaging;
using Common.Models.Package;
using DeviceService.DataSourceDevices.Camera.HuaRay;
using DeviceService.DataSourceDevices.Camera.Models;
using Serilog;
using Serilog.Events;

namespace CangFenBao.SDK
{
    /// <summary>
    /// 主SDK类，用于控制华睿相机和小车分拣系统。
    /// </summary>
    public class SortingSystemSdk : IAsyncDisposable
    {
        private readonly SdkConfig _config;
        private HuaRayCameraService? _cameraService;
        private IDisposable? _packageSubscription;
        private IDisposable? _imageSubscription;
        private SdkImageService? _imageService;
        private HttpUploadService? _httpUploadService;
        private DirectSorterService? _sorterService;
        private SdkWeightService? _weightService;
        private WeightServiceSettings? _weightSettings;

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
        /// 当收到有效的实时重量数据时触发。
        /// </summary>
        public event Action<double>? WeightUpdate;

        /// <summary>
        /// 当检测到重量低于设定的最小阈值时触发。
        /// </summary>
        public event Action<double>? NoValidWeightDetected;

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
        public SortingSystemSdk(SdkConfig config, string portName, int baudRate, int dataBits, int stopBits, int parity, int readTimeout, int writeTimeout)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _sorterService = new DirectSorterService(portName, baudRate, dataBits, stopBits, parity, readTimeout, writeTimeout);
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

                // 3. 初始化分拣机服务
                // 在 InitializeAsync 方法中，移除 _sorterService = new DirectSorterService(...) 和 sorterInitSuccess 检查
                // 只保留 _sorterService.ConnectionChanged 事件订阅
                _sorterService.ConnectionChanged += (status) =>
                {
                    Log.Information("分拣机串口连接状态: {Status}", status ? "已连接" : "已断开");
                    SorterConnectionChanged?.Invoke(status);
                };

                // 4. 初始化重量服务
                _weightService = new SdkWeightService(settingsService);
                if (_weightService.IsEnabled)
                {
                    await _weightService.StartAsync();
                    _weightSettings = settingsService.LoadSettings<WeightServiceSettings>(); // 缓存配置
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

            if (_sorterService != null)
            {
                await _sorterService.StartAsync();
            }

            if (_weightService != null && _weightService.IsEnabled)
            {
                await _weightService.StartAsync();
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
            if (_sorterService != null)
            {
                await _sorterService.StopAsync();
            }
            if (_weightService != null && _weightService.IsEnabled)
            {
                await _weightService.DisposeAsync();
            }

            IsRunning = false;
            Log.Information("SDK 服务已停止。");
        }

        /// <summary>
        /// 发送分拣机指令。
        /// </summary>
        /// <param name="command">要发送的指令字节数组。</param>
        /// <returns>如果指令发送成功，则为 true；否则为 false。</returns>
        public async Task<bool> SendSorterCommandAsync(byte[] command)
        {
            if (!IsRunning || _sorterService == null)
            {
                Log.Warning("SDK 未运行或分拣服务未初始化，无法发送指令。");
                return false;
            }
            return await _sorterService.SendCommandAsync(command);
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
            var packageReceivedTime = DateTime.Now; // 以SDK收到包的时间为基准
            Log.Information("收到原始包裹数据: 条码 {Barcode}，此时相机重量为 {Weight}g", package.Barcode, package.Weight);

            double fusedWeight;

            if (_weightService is { IsEnabled: true })
            {
                var lowerBound = packageReceivedTime.AddMilliseconds(_weightSettings!.FusionTimeRangeLowerMs);
                var upperBound = packageReceivedTime.AddMilliseconds(_weightSettings.FusionTimeRangeUpperMs);

                // 1. 尝试从稳定队列中查找匹配的重量
                var stableWeightEntry = _weightService.StableWeights
                    .Where(w => w.Timestamp >= lowerBound && w.Timestamp <= upperBound)
                    .OrderByDescending(w => w.Timestamp) // 取时间范围内最新的一个
                    .FirstOrDefault();

                if (stableWeightEntry != default)
                {
                    fusedWeight = stableWeightEntry.Weight;
                    Log.Information("成功融合稳定重量: {Weight:F2}g (时间戳: {Timestamp})", fusedWeight, stableWeightEntry.Timestamp);
                }
                else
                {
                    // 2. 如果找不到，使用最新的实时重量作为备用
                    fusedWeight = _weightService.LatestWeightInGrams;
                    Log.Warning("在时间范围 [{Lower}, {Upper}] 内未找到稳定重量，使用最新实时重量作为备用: {Weight:F2}g",
                        lowerBound.ToString("HH:mm:ss.fff"),
                        upperBound.ToString("HH:mm:ss.fff"),
                        fusedWeight);
                }
            }
            else
            {
                Log.Warning("重量服务未启用或未初始化，无法融合重量。");
                // 在此情况下，fusedWeight 将保持为 0，或使用相机自带的重量（如果存在）
                fusedWeight = package.Weight;
            }

            // 3. 将融合后的重量赋值给包裹对象
            package.SetWeight(fusedWeight / 1000.0); // SetWeight参数为千克

            // 4. 使用融合后的重量进行业务判断（例如，丢弃包裹）
            // 注意：这里的 MinimumWeightGrams 来自 SdkConfig，与重量服务的阈值是两个概念
            if (_config.MinimumWeightGrams > 0 && package.Weight < _config.MinimumWeightGrams)
            {
                PackageDiscarded?.Invoke(this, package);
                Log.Information("包裹 {Barcode} 因融合后重量 ({Weight}g) 低于业务阈值 ({Threshold}g) 被丢弃",
                    package.Barcode, package.Weight, _config.MinimumWeightGrams);
                return;
            }

            Log.Information("包裹 {Barcode} 通过重量检查，最终重量: {Weight:F2}g", package.Barcode, package.Weight);

            // 5. 触发最终事件
            PackageReady?.Invoke(this, package);

            // 6. 处理数据上传（如果启用）
            if (_config.EnableUpload)
            {
                Log.Information("启用上传功能，开始异步上传包裹 {Barcode}...", package.Barcode);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var response = await _httpUploadService!.UploadPackageAsync(package);
                        UploadCompleted?.Invoke(this, (package, response));
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "异步上传包裹 {Barcode} 时发生错误。", package.Barcode);
                        UploadCompleted?.Invoke(this, (package, null));
                    }
                });
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
            if (_sorterService != null)
            {
                await _sorterService.DisposeAsync();
            }
            if (_weightService != null)
            {
                await _weightService.DisposeAsync();
            }
            await Log.CloseAndFlushAsync(); // 确保所有日志都写入完毕
            GC.SuppressFinalize(this);
            Log.Information("SDK 资源释放完毕。");
        }
    }
}
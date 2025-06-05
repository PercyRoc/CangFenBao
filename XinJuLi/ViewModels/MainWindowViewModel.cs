using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BalanceSorting.Models;
using BalanceSorting.Service;
using Camera.Interface;
using Common.Models;
using Common.Models.Package;
using Common.Models.Settings.ChuteRules;
using Common.Services.Settings;
using Common.Services.Ui;
using History.Data;
using Serilog;
using XinJuLi.Models.ASN;
using XinJuLi.Services.ASN;
using XinJuLi.Events;

namespace XinJuLi.ViewModels;

public class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private readonly IPendulumSortService _sortService;
    private readonly IAsnService _asnService;
    private readonly IPackageHistoryDataService _packageHistoryDataService;
    private readonly IAsnCacheService _asnCacheService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _timer;
    private string _currentBarcode = string.Empty;
    private BitmapSource? _currentImage;
    private bool _disposed;
    private SystemStatus _systemStatus = new();

    // ASN订单相关
    private List<AsnOrderItem> _asnOrderItems = [];
    private string _currentAsnOrderCode = string.Empty;
    private string _currentCarCode = string.Empty;

    // SKU分配表，Key是SKU代码，Value是分配的格口编号
    private readonly Dictionary<string, int> _skuChuteMappings = [];

    // 每个格口分配的SKU数量
    private readonly Dictionary<int, int> _chuteSkuCount = [];

    // Add persistent counters
    private long _totalPackageCount;
    private long _successPackageCount;

    private long _failedPackageCount;


    public MainWindowViewModel(IDialogService dialogService,
        ICameraService cameraService,
        ISettingsService settingsService,
        IPendulumSortService sortService,
        INotificationService notificationService,
        IAsnService asnService,
        IEventAggregator eventAggregator,
        IPackageHistoryDataService packageHistoryDataService,
        IAsnCacheService asnCacheService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _settingsService = settingsService;
        _sortService = sortService;
        _notificationService = notificationService;
        _asnService = asnService;
        _packageHistoryDataService = packageHistoryDataService;
        _asnCacheService = asnCacheService;
        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        OpenHistoryCommand = new DelegateCommand(ExecuteOpenHistory);

        // 初始化系统状态更新定时器
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        // 初始化设备状态
        InitializeDeviceStatuses();

        // 初始化统计数据
        InitializeStatisticsItems();

        // 初始化包裹信息
        InitializePackageInfoItems();

        // 订阅相机连接状态事件
        _cameraService.ConnectionChanged += OnCameraConnectionChanged;

        // 订阅图像流
        _subscriptions.Add(_cameraService.ImageStreamWithId
            .ObserveOn(TaskPoolScheduler.Default) // 使用任务池调度器
            .Subscribe(imageData =>
            {
                try
                {
                    var bitmapSource = imageData;

                    Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                    {
                        try
                        {
                            // 更新UI
                            CurrentImage = bitmapSource.Image;
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "更新UI图像时发生错误");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理图像流时发生错误");
                }
            }));
        _sortService.DeviceConnectionStatusChanged += OnDeviceConnectionStatusChanged;
        // 订阅包裹流
        _subscriptions.Add(_cameraService.PackageStream
            .Subscribe(package => { Application.Current.Dispatcher.BeginInvoke(() => OnPackageInfo(package)); }));

        // 订阅ASN订单接收事件（现在用于处理用户选择的ASN单）
        _subscriptions.Add(eventAggregator.GetEvent<AsnOrderReceivedEvent>()
            .Subscribe(asnInfo => { Application.Current.Dispatcher.BeginInvoke(() => OnAsnOrderReceived(asnInfo)); }));

        // 主动查询一次相机和摆轮分拣设备状态
        QueryAndUpdateDeviceStatuses();

        // 订阅ASN缓存变更事件
        _asnCacheService.CacheChanged += OnAsnCacheChanged;
    }

    public DelegateCommand OpenSettingsCommand { get; }
    public DelegateCommand OpenHistoryCommand { get; }

    public string CurrentBarcode
    {
        get => _currentBarcode;
        private set => SetProperty(ref _currentBarcode, value);
    }

    public BitmapSource? CurrentImage
    {
        get => _currentImage;
        private set => SetProperty(ref _currentImage, value);
    }

    public SystemStatus SystemStatus
    {
        get => _systemStatus;
        private set => SetProperty(ref _systemStatus, value);
    }

    /// <summary>
    /// 当前ASN订单编码
    /// </summary>
    public string CurrentAsnOrderCode
    {
        get => _currentAsnOrderCode;
        private set => SetProperty(ref _currentAsnOrderCode, value);
    }

    /// <summary>
    /// 当前车牌号
    /// </summary>
    public string CurrentCarCode
    {
        get => _currentCarCode;
        private set => SetProperty(ref _currentCarCode, value);
    }

    /// <summary>
    /// ASN订单项集合
    /// </summary>
    public List<AsnOrderItem> AsnOrderItems
    {
        get => _asnOrderItems;
        private set => SetProperty(ref _asnOrderItems, value);
    }

    /// <summary>
    /// 缓存的ASN单数量
    /// </summary>
    public int CachedAsnOrderCount => _asnCacheService.Count;

    public ObservableCollection<PackageInfo> PackageHistory { get; } = [];
    public ObservableCollection<StatisticsItem> StatisticsItems { get; } = [];
    public ObservableCollection<DeviceStatus> DeviceStatuses { get; } = [];
    public ObservableCollection<PackageInfoItem> PackageInfoItems { get; } = [];

    /// <summary>
    /// 加载并应用ASN订单数据用于分拣
    /// </summary>
    /// <param name="asnOrderInfo">ASN订单信息</param>
    public void LoadAsnOrderInfo(AsnOrderInfo asnOrderInfo)
    {
        try
        {
            if (asnOrderInfo.Items.Count == 0)
            {
                Log.Warning("无法加载ASN订单数据：数据为空或无商品项");
                return;
            }

            // 更新属性
            CurrentAsnOrderCode = asnOrderInfo.OrderCode;
            CurrentCarCode = asnOrderInfo.CarCode;
            AsnOrderItems = [.. asnOrderInfo.Items];

            Log.Information("已加载ASN订单数据用于分拣: {OrderCode}, 车牌: {CarCode}, 商品数量: {ItemsCount}",
                asnOrderInfo.OrderCode, asnOrderInfo.CarCode, asnOrderInfo.Items.Count);

            // 记录所有商品项到日志
            foreach (var item in asnOrderInfo.Items)
            {
                Log.Debug("ASN订单商品项: ItemCode={ItemCode}, SkuCode={SkuCode}, SkuName={SkuName}",
                    item.ItemCode, item.SkuCode, item.SkuName);
            }

            // 清空之前的SKU-格口映射，不预先分配格口
            _skuChuteMappings.Clear();
            _chuteSkuCount.Clear();

            // 通知UI更新
            RaisePropertyChanged(nameof(CurrentAsnOrderCode));
            RaisePropertyChanged(nameof(CurrentCarCode));
            RaisePropertyChanged(nameof(AsnOrderItems));

            _notificationService.ShowSuccess($"已加载ASN单：{asnOrderInfo.OrderCode}，包含{asnOrderInfo.Items.Count}个货品");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载ASN订单数据时发生错误");
            _notificationService.ShowError("加载ASN订单数据失败：" + ex.Message);
        }
    }



    /// <summary>
    /// 查找可用的格口（SKU数量少于2的）
    /// </summary>
    /// <returns>可用格口号，如果没有则返回-1</returns>
    private int FindAvailableChute()
    {
        // 按需加载配置
        var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();
        // 从1开始循环到格口数量，每次增加2以只分配奇数格口
        for (var i = 1; i <= chuteSettings.ChuteCount; i += 2)
        {
            // 如果格口不存在或者SKU数量小于2，则可用
            if (!_chuteSkuCount.TryGetValue(i, out var value) || value < 2)
                return i;
        }

        // 没有可用奇数格口
        return -1;
    }

    /// <summary>
    /// 处理接收到的包裹信息
    /// </summary>
    private async void OnPackageInfo(PackageInfo package)
    {
        try
        {
            var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();
            var asnSettings = _settingsService.LoadSettings<AsnSettings>(); // 加载ASN设置

            Log.Information("接收到包裹信息: {Barcode}, 重量: {称重模块}kg", package.Barcode, package.Weight);

            // 更新当前条码
            CurrentBarcode = package.Barcode;

            // 更新包裹信息显示 (初始状态)
            UpdatePackageInfoItems(package);

            // 初始化处理状态
            if (string.IsNullOrEmpty(package.Barcode))
            {
                // 无条码，设置为NoRead异常
                package.SetStatus("无条码");
                package.ErrorMessage = "无法读取条码";
                package.SetChute(chuteSettings.NoReadChuteNumber);

                Log.Warning("包裹条码为空，分配到NoRead格口: {ChuteNumber}", chuteSettings.NoReadChuteNumber);
                ProcessPackageWithError(package, chuteSettings.NoReadChuteNumber, "无条码");
                // 记录包裹 (无条码也记录)
                SavePackage(package);
                return;
            }

            // 暂时注释掉复核接口调用
            /*
            // 构建扫码复核请求
            var reviewRequest = new MaterialReviewRequest
            {
                SystemCode = asnSettings.SystemCode,
                HouseCode = asnSettings.HouseCode,
                BoxCode = package.Barcode,
                ExitArea = asnSettings.ReviewExitArea // 使用设置中的月台值
            };

            Log.Information("发送扫码复核请求: {@ReviewRequest}", reviewRequest);

            // 调用扫码复核接口
            var reviewResponse = await _asnService.ProcessMaterialReview(reviewRequest);

            // 根据接口返回处理包裹
            if (reviewResponse.Success == false)
            {
                // 复核失败
                Log.Warning("包裹 {Barcode} 扫码复核失败: {Message}", package.Barcode, reviewResponse.Message);
                package.SetStatus("复核失败");
                package.ErrorMessage = $"复核失败: {reviewResponse.Message}";
                package.SetChute(chuteSettings.ErrorChuteNumber);

                ProcessPackageWithError(package, chuteSettings.ErrorChuteNumber, package.ErrorMessage);
                SavePackage(package);

                // 计数更新
                _totalPackageCount++;
                _failedPackageCount++;
                UpdateStatisticsItems();
                return;
            }

            // 复核成功，继续执行原有的SKU匹配和分拣逻辑
            Log.Information("包裹 {Barcode} 扫码复核成功，继续进行SKU匹配和分拣", package.Barcode);
            */

            // 跳过复核，直接进行SKU匹配和分拣逻辑
            Log.Information("包裹 {Barcode} 跳过复核，直接进行SKU匹配和分拣", package.Barcode);

            // 如果没有ASN订单缓存，无法匹配SKU
            if (AsnOrderItems.Count == 0 || string.IsNullOrEmpty(CurrentAsnOrderCode))
            {
                Log.Warning("没有加载的ASN订单数据，无法为包裹 {Barcode} 匹配SKU", package.Barcode);
                package.SetStatus("无ASN单数据");
                package.ErrorMessage = "无ASN单数据";
                package.SetChute(chuteSettings.ErrorChuteNumber);

                // 分拣操作 - 错误格口
                ProcessPackageWithError(package, chuteSettings.ErrorChuteNumber, "无ASN单数据");
                // 记录包裹
                SavePackage(package);

                // 计数更新
                _totalPackageCount++;
                _failedPackageCount++;
                UpdateStatisticsItems();
                return;
            }

            // 查找对应的订单项，通常条码会与itemCode匹配
            // 更正：根据日志显示，包裹条码与SkuCode匹配更为合理
            var matchedItem = AsnOrderItems.FirstOrDefault(item =>
                item.SkuCode.Equals(package.Barcode, StringComparison.OrdinalIgnoreCase));

            if (matchedItem == null)
            {
                Log.Warning("包裹 {Barcode} 在ASN订单 {OrderCode} 中找不到匹配的货品",
                    package.Barcode, CurrentAsnOrderCode);
                package.SetStatus("ASN单中无匹配");
                package.ErrorMessage = "ASN单中无匹配";
                package.SetChute(chuteSettings.ErrorChuteNumber);

                // 分拣操作 - 错误格口
                ProcessPackageWithError(package, chuteSettings.ErrorChuteNumber, "ASN单中无匹配");
                // 记录包裹
                SavePackage(package);

                // 计数更新
                _totalPackageCount++;
                _failedPackageCount++;
                UpdateStatisticsItems();
                return;
            }

            // 如果到达此处，表示ASN数据存在，且匹配到了货品
            // 现在，根据SKU映射确定格口，如果未分配则动态分配
            if (!_skuChuteMappings.TryGetValue(matchedItem.SkuCode, out var chuteNumber))
            {
                // SKU未分配格口，尝试动态分配
                Log.Information("SKU {SkuCode} 未分配格口，开始动态分配", matchedItem.SkuCode);
                
                var availableChute = FindAvailableChute();
                if (availableChute > 0)
                {
                    // 分配格口给该SKU
                    _skuChuteMappings[matchedItem.SkuCode] = availableChute;
                    _chuteSkuCount.TryAdd(availableChute, 0);
                    _chuteSkuCount[availableChute]++;
                    
                    chuteNumber = availableChute;
                    Log.Information("SKU {SkuCode} 动态分配到格口 {Chute}", matchedItem.SkuCode, availableChute);
                }
                else
                {
                    // 没有可用格口
                    Log.Warning("没有可用格口分配给SKU {SkuCode}，将分配到错误格口", matchedItem.SkuCode);
                    package.SetStatus("无可用格口");
                    package.ErrorMessage = $"SKU {matchedItem.SkuCode} 无可用格口";
                    package.SetChute(chuteSettings.ErrorChuteNumber);
                    ProcessPackageWithError(package, chuteSettings.ErrorChuteNumber, package.ErrorMessage);
                    _totalPackageCount++;
                    _failedPackageCount++;
                    UpdateStatisticsItems();
                    SavePackage(package);
                    return;
                }
            }

            package.SetChute(chuteNumber);
            package.SetStatus("成功");
            package.ErrorMessage = string.Empty; // 清除任何之前的错误信息

            // 更新UI显示（成功状态）
            UpdatePackageInfoItemsWithSku(matchedItem, chuteNumber);

            // 分拣操作 - 成功格口
            ProcessPackageSuccess(package);

            // 记录包裹
            SavePackage(package);

            // 计数更新
            _totalPackageCount++;
            _successPackageCount++;
            UpdateStatisticsItems();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹信息时发生错误: {Barcode}", package.Barcode);

            try
            {
                // 发生异常，分配到异常格口
                {
                    // 再次加载配置以确保在异常处理中也使用最新值
                    var currentChuteSettings = _settingsService.LoadSettings<ChuteSettings>();
                    package.SetChute(currentChuteSettings.ErrorChuteNumber);
                    package.SetStatus("处理异常");
                    package.ErrorMessage = $"处理发生异常: {ex.Message}";

                    // 分拣操作 - 异常格口
                    ProcessPackageWithError(package, currentChuteSettings.ErrorChuteNumber, "处理异常");

                    // 记录包裹
                    SavePackage(package);

                    // 计数更新
                    _totalPackageCount++;
                    _failedPackageCount++;

                    // 更新统计数据
                    UpdateStatisticsItems();
                }
            }
            catch (Exception innerEx)
            {
                Log.Error(innerEx, "处理包裹异常时发生二次错误");
            }
        }
    }

    /// <summary>
    /// 处理成功的包裹
    /// </summary>
    private void ProcessPackageSuccess(PackageInfo package)
    {
        try
        {
            // 执行分拣，调用分拣服务
            _sortService.ProcessPackage(package);
            Log.Debug("执行分拣操作: 包裹 {Barcode} 到格口 {Chute}", package.Barcode, package.ChuteNumber);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行分拣操作失败: {Barcode} 到格口 {Chute}", package.Barcode, package.ChuteNumber);
        }
    }

    /// <summary>
    /// 处理异常包裹
    /// </summary>
    private void ProcessPackageWithError(PackageInfo package, int errorChuteNumber, string errorReason)
    {
        try
        {
            // 设置包裹的错误信息
            package.SetChute(errorChuteNumber);
            package.SetStatus(errorReason);

            // 更新UI显示
            UpdatePackageInfoItemsWithError(errorChuteNumber, errorReason);

            // 执行分拣，调用分拣服务
            _sortService.ProcessPackage(package);
            Log.Debug("执行异常分拣操作: 包裹 {Barcode} 到错误格口 {Chute}, 原因: {Reason}",
                package.Barcode, errorChuteNumber, errorReason);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行异常分拣操作失败: {Barcode} 到错误格口 {Chute}", package.Barcode, errorChuteNumber);
        }
    }

    /// <summary>
    /// 更新包裹信息显示
    /// </summary>
    private void UpdatePackageInfoItems(PackageInfo package)
    {
        try
        {
            // 更新重量显示
            var weightItem = PackageInfoItems.FirstOrDefault(x => x.Label == "重量");
            if (weightItem != null)
                weightItem.Value = package.Weight.ToString("F2");

            // 更新时间显示
            var timeItem = PackageInfoItems.FirstOrDefault(x => x.Label == "时间");
            if (timeItem != null)
                timeItem.Value = DateTime.Now.ToString("HH:mm:ss");

            // 更新状态显示
            var statusItem = PackageInfoItems.FirstOrDefault(x => x.Label == "状态");
            if (statusItem != null)
                statusItem.Value = package.StatusDisplay;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新包裹信息显示时发生错误");
        }
    }

    /// <summary>
    /// 使用SKU信息更新包裹显示
    /// </summary>
    private void UpdatePackageInfoItemsWithSku(AsnOrderItem item, int chuteNumber)
    {
        try
        {
            // 更新格口显示
            var chuteItem = PackageInfoItems.FirstOrDefault(x => x.Label == "格口");
            if (chuteItem != null)
            {
                chuteItem.Value = chuteNumber.ToString();
                chuteItem.Description = $"{item.SkuName} ({item.SkuCode})";
            }

            // 更新状态显示
            var statusItem = PackageInfoItems.FirstOrDefault(x => x.Label == "状态");
            if (statusItem != null)
                statusItem.Value = "成功";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "使用SKU信息更新包裹显示时发生错误");
        }
    }

    /// <summary>
    /// 使用错误信息更新包裹显示
    /// </summary>
    private void UpdatePackageInfoItemsWithError(int errorChuteNumber, string errorReason)
    {
        try
        {
            // 更新格口显示
            var chuteItem = PackageInfoItems.FirstOrDefault(x => x.Label == "格口");
            if (chuteItem != null)
            {
                chuteItem.Value = errorChuteNumber.ToString();
                chuteItem.Description = $"错误格口: {errorReason}";
            }

            // 更新状态显示
            var statusItem = PackageInfoItems.FirstOrDefault(x => x.Label == "状态");
            if (statusItem != null)
                statusItem.Value = "异常";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新包裹错误显示时发生错误");
        }
    }

    /// <summary>
    /// 更新统计数据
    /// </summary>
    private void UpdateStatisticsItems()
    {
        try
        {
            // 更新总包裹数
            var totalItem = StatisticsItems.FirstOrDefault(x => x.Label == "总包裹数");
            if (totalItem != null)
                totalItem.Value = _totalPackageCount.ToString();

            // 更新成功数
            var successItem = StatisticsItems.FirstOrDefault(x => x.Label == "成功数");
            if (successItem != null)
                successItem.Value = _successPackageCount.ToString();

            // 更新异常数
            var failedItem = StatisticsItems.FirstOrDefault(x => x.Label == "异常");
            if (failedItem != null)
                failedItem.Value = _failedPackageCount.ToString();

            // 更新处理速率 - 这里可以进一步实现，比如计算每小时处理量
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新统计数据时发生错误");
        }
    }

    /// <summary>
    /// 保存包裹记录
    /// </summary>
    private async void SavePackage(PackageInfo package)
    {
        try
        {
            var record = PackageHistoryRecord.FromPackageInfo(package);
            await _packageHistoryDataService.AddPackageAsync(record);
            Log.Debug("包裹记录已保存: {Barcode}", package.Barcode);

            // 添加到历史记录列表
            Application.Current.Dispatcher.Invoke(() =>
            {
                // 限制历史记录数量
                if (PackageHistory.Count >= 1000)
                    PackageHistory.RemoveAt(PackageHistory.Count - 1);

                // 在开头添加新记录
                PackageHistory.Insert(0, package);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存包裹记录时发生错误: {Barcode}", package.Barcode);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialog");
    }

    private void ExecuteOpenHistory()
    {
        _dialogService.ShowDialog("PackageHistoryDialogView", null, (Action<IDialogResult>?)null);
    }



    private void Timer_Tick(object? sender, EventArgs e)
    {
        SystemStatus = SystemStatus.GetCurrentStatus();
    }

    private void InitializeDeviceStatuses()
    {
        try
        {
            Log.Debug("开始初始化设备状态列表");

            // 添加相机状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "相机",
                Status = "未连接",
                Icon = "Camera24",
                StatusColor = "#F44336" // 红色表示未连接
            });

            // 加载分拣配置
            var configuration = _settingsService.LoadSettings<PendulumSortConfig>();

            // 添加触发光电状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "触发光电",
                Status = "未连接",
                Icon = "Lightbulb24",
                StatusColor = "#F44336" // 红色表示未连接
            });

            // 添加分拣光电状态
            foreach (var photoelectric in configuration.SortingPhotoelectrics)
                DeviceStatuses.Add(new DeviceStatus
                {
                    Name = photoelectric.Name,
                    Status = "未连接",
                    Icon = "Lightbulb24",
                    StatusColor = "#F44336" // 红色表示未连接
                });

            Log.Debug("设备状态列表初始化完成，共 {Count} 个设备", DeviceStatuses.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化设备状态列表时发生错误");
        }
    }

    private void InitializeStatisticsItems()
    {
        StatisticsItems.Add(new StatisticsItem(
            label: "总包裹数",
            value: "0",
            unit: "个",
            description: "累计处理包裹总数",
            icon: "BoxMultiple24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            label: "成功数",
            value: "0",
            unit: "个",
            description: "处理成功的包裹数量",
            icon: "CheckmarkCircle24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            label: "异常",
            value: "0",
            unit: "个",
            description: "其他异常包裹数量",
            icon: "Alert24"
        ));

        StatisticsItems.Add(new StatisticsItem
        (
            label: "处理速率",
            value: "0",
            unit: "个/小时",
            description: "每小时处理包裹数量",
            icon: "ArrowTrendingLines24"
        ));

        // 添加峰值效率统计
        StatisticsItems.Add(new StatisticsItem
        (
            label: "峰值效率",
            value: "0",
            unit: "个/小时",
            description: "最高处理速率",
            icon: "Trophy24"
        ));
    }

    private void InitializePackageInfoItems()
    {
        PackageInfoItems.Add(new PackageInfoItem(
            label: "重量",
            value: "0.00",
            unit: "kg",
            description: "包裹重量",
            icon: "Scales24"
        ));
        PackageInfoItems.Add(new PackageInfoItem(
            label: "格口",
            value: "--",
            description: "目标分拣位置",
            icon: "ArrowCircleDown24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            label: "时间",
            value: "--:--:--",
            description: "处理时间",
            icon: "Timer24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            label: "状态",
            value: "等待",
            description: "处理状态",
            icon: "Alert24"
        ));
    }

    private void OnCameraConnectionChanged(string? deviceId, bool isConnected)
    {
        try
        {
            var cameraStatus = DeviceStatuses.FirstOrDefault(static x => x.Name == "相机");
            if (cameraStatus == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                cameraStatus.Status = isConnected ? "已连接" : "已断开";
                cameraStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新相机状态时发生错误");
        }
    }

    private void OnDeviceConnectionStatusChanged(object? sender, (string Name, bool Connected) e)
    {
        Log.Debug("设备连接状态变更: {Name} -> {Status}", e.Name, e.Connected ? "已连接" : "已断开");

        Application.Current.Dispatcher.Invoke(() =>
        {
            var deviceStatus = DeviceStatuses.FirstOrDefault(x => x.Name == e.Name);
            if (deviceStatus == null)
            {
                Log.Warning("未找到设备状态项: {Name}", e.Name);
                return;
            }

            deviceStatus.Status = e.Connected ? "已连接" : "已断开";
            deviceStatus.StatusColor = e.Connected ? "#4CAF50" : "#F44336"; // 绿色表示正常，红色表示断开
            Log.Debug("设备状态已更新: {Name} -> {Status}", e.Name, deviceStatus.Status);
        });
    }

    /// <summary>
    /// 处理ASN订单接收事件（用户选择后的处理）
    /// </summary>
    private void OnAsnOrderReceived(AsnOrderInfo asnInfo)
    {
        Log.Information("MainWindowViewModel收到ASN订单选择事件: {OrderCode}", asnInfo.OrderCode);
        
        // 加载选择的ASN单信息并清空格口映射
        LoadAsnOrderInfo(asnInfo);
        
        // 从缓存中移除已选择的ASN单
        _asnCacheService.RemoveAsnOrder(asnInfo.OrderCode);
    }

    /// <summary>
    /// 处理ASN缓存变更事件
    /// </summary>
    private void OnAsnCacheChanged(object? sender, AsnCacheChangedEventArgs e)
    {
        // 在UI线程中更新缓存数量
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            RaisePropertyChanged(nameof(CachedAsnOrderCount));
        });
    }

    /// <summary>
    /// 主动查询一次相机和摆轮分拣设备状态，并更新DeviceStatuses
    /// </summary>
    private void QueryAndUpdateDeviceStatuses()
    {
        try
        {
            // 查询相机状态
            var cameraStatus = DeviceStatuses.FirstOrDefault(x => x.Name == "相机");
            if (cameraStatus != null)
            {
                cameraStatus.Status = _cameraService.IsConnected ? "已连接" : "已断开";
                cameraStatus.StatusColor = _cameraService.IsConnected ? "#4CAF50" : "#F44336";
            }

            // 查询摆轮分拣设备状态
            var deviceStates = _sortService.GetAllDeviceConnectionStates();
            foreach (var deviceStatus in DeviceStatuses)
            {
                if (deviceStatus.Name == "相机") continue; // 相机已处理
                if (!deviceStates.TryGetValue(deviceStatus.Name, out var connected)) continue;
                deviceStatus.Status = connected ? "已连接" : "已断开";
                deviceStatus.StatusColor = connected ? "#4CAF50" : "#F44336";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "主动查询设备状态时发生错误");
        }
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
            try
            {
                // 停止定时器（UI线程操作）
                if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
                    Application.Current.Dispatcher.Invoke(_timer.Stop);
                else
                    _timer.Stop();

                // 停止分拣服务
                if (_sortService.IsRunning())
                    try
                    {
                        // 使用超时避免无限等待
                        var stopTask = _sortService.StopAsync();
                        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
                        var completedTask = Task.WhenAny(stopTask, timeoutTask).Result;

                        if (completedTask == stopTask)
                            Log.Information("摆轮分拣服务已停止");
                        else
                            Log.Warning("摆轮分拣服务停止超时");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "停止摆轮分拣服务时发生错误");
                    }

                // 释放分拣服务资源
                if (_sortService is IDisposable disposableSortService)
                {
                    disposableSortService.Dispose();
                    Log.Information("摆轮分拣服务资源已释放");
                }

                // 取消订阅事件
                _sortService.DeviceConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
                _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
                _asnCacheService.CacheChanged -= OnAsnCacheChanged;

                // 释放订阅
                foreach (var subscription in _subscriptions)
                    try
                    {
                        subscription.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "释放订阅时发生错误");
                    }

                _subscriptions.Clear();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放资源时发生错误");
            }

        _disposed = true;
    }
}
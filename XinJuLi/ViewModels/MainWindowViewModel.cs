using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
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
using Microsoft.Win32;

namespace XinJuLi.ViewModels;

public class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private readonly IPendulumSortService _sortService;
    private readonly IPackageHistoryDataService _packageHistoryDataService;

    private readonly IExcelImportService _excelImportService;
    private readonly IAsnCacheService _asnCacheService;
    private readonly IAsnStorageService _asnStorageService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _timer;
    private string _currentBarcode = string.Empty;
    private BitmapSource? _currentImage;
    private bool _disposed;
    private SystemStatus _systemStatus = new();

    // Event Aggregator
    private readonly IEventAggregator _eventAggregator;



    // SKU分配表，Key是SKU代码，Value是分配的格口编号
    private readonly Dictionary<string, int> _skuChuteMappings = [];

    // 格口到SKU列表的映射，Key是格口编号，Value是分配给该格口的SKU代码列表
    private readonly Dictionary<int, List<string>> _chuteToSkusMapping = [];

    // 大区编码到格口的映射缓存
    private readonly Dictionary<string, int> _areaCodeChuteMappings = [];

    // 每个格口分配的SKU数量
    private readonly Dictionary<int, int> _chuteSkuCount = [];

    // SKU格口映射配置实例（用于持久化）
    private SkuChuteMapping _skuChuteMappingConfig = new();

    // Add persistent counters
    private long _totalPackageCount;
    private long _successPackageCount;
    private long _failedPackageCount;

    // 效率计算相关
    private DateTime _sessionStartTime;
    private DateTime _lastPackageTime;
    private readonly Queue<DateTime> _recentPackageTimes = new();
    private double _peakEfficiency;
    private readonly object _efficiencyLock = new();


    public MainWindowViewModel(IDialogService dialogService,
        ICameraService cameraService,
        ISettingsService settingsService,
        IPendulumSortService sortService,
        INotificationService notificationService,
        IEventAggregator eventAggregator,
        IPackageHistoryDataService packageHistoryDataService,
        IAsnCacheService asnCacheService,
        IAsnStorageService asnStorageService,
        IExcelImportService excelImportService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _settingsService = settingsService;
        _sortService = sortService;
        _notificationService = notificationService;
        _packageHistoryDataService = packageHistoryDataService;
        _asnCacheService = asnCacheService;
        _asnStorageService = asnStorageService;
        _excelImportService = excelImportService;
        _eventAggregator = eventAggregator;

        // 初始化会话开始时间
        _sessionStartTime = DateTime.Now;

        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        OpenHistoryCommand = new DelegateCommand(ExecuteOpenHistory);
        OpenAsnSelectionCommand = new DelegateCommand(ExecuteOpenAsnSelection);
        ImportConfigCommand = new DelegateCommand(ExecuteImportConfig);
        ClearChuteCommand = new DelegateCommand<ChuteStatusItem>(ExecuteClearChute);

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

        // 初始化格口状态
        InitializeChuteStatuses();

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
            .Subscribe(asnInfo => { Application.Current.Dispatcher.BeginInvoke(() => OnAsnOrderSelected(asnInfo)); }));

        // 订阅ASN单已添加到缓存事件，用于弹出选择对话框
        _subscriptions.Add(eventAggregator.GetEvent<AsnOrderAddedToCacheEvent>()
            .Subscribe(asnInfo => { Application.Current.Dispatcher.BeginInvoke(() => OnAsnOrderAddedToCache(asnInfo)); }));

        // 主动查询一次相机和摆轮分拣设备状态
        QueryAndUpdateDeviceStatuses();

        // 订阅ASN缓存变更事件
        _asnCacheService.CacheChanged += OnAsnCacheChanged;

        // 测试：分配一个格口用于验证UI显示
        TestAssignChute();

        // 初始化ASN相关信息
        InitializeAsnInfo();

        // 加载现有的格口大区编码配置（已注释，改为SKU分拣模式）
        // LoadExistingAreaCodeConfig();
    }

    /// <summary>
    /// 初始化ASN相关信息
    /// </summary>
    private void InitializeAsnInfo()
    {
        try
        {
            Log.Information("开始初始化ASN相关信息");

            // 初始化ASN属性
            CurrentAsnOrderCode = "未选择";
            CurrentCarCode = "未选择";
            CachedAsnOrderCount = 0; // 暂时设为0，后续通过事件更新

            // 加载上次保存的SKU格口映射
            LoadSkuChuteMappingFromPersistence();

            Log.Information("ASN信息初始化完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化ASN信息时发生错误");
        }
    }

    /// <summary>
    /// 加载现有的格口大区编码配置
    /// </summary>
    private void LoadExistingAreaCodeConfig()
    {
        try
        {
            Log.Information("开始加载现有的格口大区编码配置");

            var config = _settingsService.LoadSettings<ChuteAreaConfig>();
            if (config != null && config.Items.Count > 0)
            {
                // 验证配置完整性
                if (ValidateConfigOnStartup(config))
                {
                    // 更新内存中的映射缓存
                    UpdateAreaCodeMappingCache(config);

                    // 更新格口状态显示
                    UpdateChuteStatusDisplay(config);

                    Log.Information("成功加载现有配置: {Count}个映射项，导入时间: {ImportTime}", 
                        config.Items.Count, config.ImportTime);
                    
                    _notificationService.ShowSuccess($"已加载现有配置：{config.Items.Count}个映射项");
                }
                else
                {
                    Log.Warning("配置文件存在问题，尝试恢复备份");
                    _ = Task.Run(RestoreConfigBackupAsync);
                }
            }
            else
            {
                Log.Information("没有找到现有的格口大区编码配置，等待用户导入");
                _notificationService.ShowWarning("请导入格口配置文件以开始使用");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载现有格口大区编码配置时发生错误");
            _notificationService.ShowError("配置加载失败，请检查配置文件或重新导入");
            
            // 配置加载失败时尝试恢复备份
            _ = Task.Run(RestoreConfigBackupAsync);
        }
    }

    /// <summary>
    /// 启动时验证配置完整性
    /// </summary>
    /// <param name="config">配置对象</param>
    /// <returns>配置是否有效</returns>
    private bool ValidateConfigOnStartup(ChuteAreaConfig config)
    {
        try
        {
            // 基本验证
            if (config.Items == null || config.Items.Count == 0)
            {
                Log.Warning("配置验证失败: 配置项为空");
                return false;
            }

            // 验证关键数据完整性
            var invalidItems = config.Items.Where(item => 
                item.ChuteNumber < 2 || 
                item.ChuteNumber > 100 || 
                string.IsNullOrWhiteSpace(item.AreaCode)).ToList();

            if (invalidItems.Count > 0)
            {
                Log.Warning("配置验证失败: 发现 {Count} 个无效配置项", invalidItems.Count);
                return false;
            }

            // 验证是否有重复项
            var duplicateChutes = config.Items.GroupBy(x => x.ChuteNumber).Where(g => g.Count() > 1).Any();
            var duplicateAreaCodes = config.Items.GroupBy(x => x.AreaCode).Where(g => g.Count() > 1).Any();

            if (duplicateChutes || duplicateAreaCodes)
            {
                Log.Warning("配置验证失败: 发现重复的格口编号或大区编码");
                return false;
            }

            Log.Information("启动时配置验证通过");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动时配置验证发生错误");
            return false;
        }
    }

    /// <summary>
    /// 从持久化存储加载SKU格口映射
    /// </summary>
    private void LoadSkuChuteMappingFromPersistence()
    {
        try
        {
            Log.Information("开始加载SKU格口映射");

            var savedMapping = _settingsService.LoadSettings<SkuChuteMapping>();
            if (savedMapping != null && savedMapping.Items.Count > 0)
            {
                // 验证映射是否有效（格口号是否在合理范围内）
                var validItems = savedMapping.Items.Where(item => 
                    !string.IsNullOrWhiteSpace(item.Sku) && 
                    item.ChuteNumber > 0 && 
                    item.ChuteNumber <= 100).ToList();

                if (validItems.Count > 0)
                {
                    // 清空现有映射
                    _skuChuteMappings.Clear();
                    _chuteToSkusMapping.Clear();

                    // 恢复映射关系
                    foreach (var item in validItems)
                    {
                        _skuChuteMappings[item.Sku] = item.ChuteNumber;
                        
                        // 更新格口到SKU的反向映射
                        if (!_chuteToSkusMapping.ContainsKey(item.ChuteNumber))
                        {
                            _chuteToSkusMapping[item.ChuteNumber] = [];
                        }
                        _chuteToSkusMapping[item.ChuteNumber].Add(item.Sku);
                    }

                    // 更新格口状态显示
                    UpdateChuteStatusFromMapping();

                    // 更新配置信息
                    _skuChuteMappingConfig = savedMapping;
                    CurrentAsnOrderCode = string.IsNullOrEmpty(savedMapping.AsnOrderCode) ? "未选择" : savedMapping.AsnOrderCode;
                    CurrentCarCode = string.IsNullOrEmpty(savedMapping.CarCode) ? "未选择" : savedMapping.CarCode;

                    Log.Information("成功加载SKU格口映射: {Count}个映射项，ASN单号: {AsnCode}, 保存时间: {SaveTime}", 
                        validItems.Count, savedMapping.AsnOrderCode, savedMapping.SaveTime);
                    
                    _notificationService.ShowSuccess($"已恢复上次的SKU映射：{validItems.Count}个映射项");
                }
                else
                {
                    Log.Warning("保存的SKU映射中没有有效项目");
                }
            }
            else
            {
                Log.Information("没有找到保存的SKU格口映射");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载SKU格口映射时发生错误");
            _notificationService.ShowWarning("恢复SKU映射失败，请重新进行分拣配置");
        }
    }

    /// <summary>
    /// 根据映射关系更新格口状态显示
    /// </summary>
    private void UpdateChuteStatusFromMapping()
    {
        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // 先清空所有格口状态
                foreach (var chuteStatus in ChuteStatuses)
                {
                    chuteStatus.Clear();
                }

                // 根据映射关系更新格口状态
                foreach (var mapping in _chuteToSkusMapping)
                {
                    var chuteNumber = mapping.Key;
                    var skus = mapping.Value;

                    // 根据实际格口号转换为显示格口号
                    // 实际格口号 = 2 × 配置格口号 - 1，所以配置格口号 = (实际格口号 + 1) / 2
                    var displayChuteNumber = (chuteNumber + 1) / 2;
                    var chuteStatus = ChuteStatuses.FirstOrDefault(x => x.ChuteNumber == displayChuteNumber);

                    if (chuteStatus != null)
                    {
                        foreach (var sku in skus)
                        {
                            chuteStatus.AssignCategory(sku);
                        }
                        Log.Debug("恢复格口{DisplayChute}(实际格口{ActualChute})状态: SKU数量{SkuCount}", 
                            displayChuteNumber, chuteNumber, skus.Count);
                    }
                    else
                    {
                        Log.Warning("未找到对应的格口状态项: 配置格口{DisplayChute}, 实际格口{ActualChute}",
                            displayChuteNumber, chuteNumber);
                    }
                }
            });

            Log.Information("格口状态显示更新完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新格口状态显示时发生错误");
        }
    }

    /// <summary>
    /// 保存SKU格口映射到持久化存储
    /// </summary>
    private void SaveSkuChuteMappingToPersistence()
    {
        try
        {
            // 更新配置对象
            _skuChuteMappingConfig.Items.Clear();
            foreach (var mapping in _skuChuteMappings)
            {
                _skuChuteMappingConfig.AddOrUpdateItem(mapping.Key, mapping.Value);
            }

            _skuChuteMappingConfig.AsnOrderCode = CurrentAsnOrderCode;
            _skuChuteMappingConfig.CarCode = CurrentCarCode;
            _skuChuteMappingConfig.SaveTime = DateTime.Now;

            // 保存到文件
            _settingsService.SaveSettings(_skuChuteMappingConfig);

            Log.Debug("SKU格口映射已保存: {Count}个映射项", _skuChuteMappingConfig.Items.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存SKU格口映射时发生错误");
        }
    }

    /// <summary>
    /// 清空SKU格口映射持久化存储
    /// </summary>
    private void ClearSkuChuteMappingPersistence()
    {
        try
        {
            _skuChuteMappingConfig.Clear();
            _settingsService.SaveSettings(_skuChuteMappingConfig);

            Log.Information("已清空SKU格口映射持久化存储");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "清空SKU格口映射存储时发生错误");
        }
    }

    public DelegateCommand OpenSettingsCommand { get; }
    public DelegateCommand OpenHistoryCommand { get; }
    public DelegateCommand OpenAsnSelectionCommand { get; }
    public DelegateCommand ImportConfigCommand { get; }
    public DelegateCommand<ChuteStatusItem> ClearChuteCommand { get; }

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



    public ObservableCollection<PackageInfo> PackageHistory { get; } = [];
    public ObservableCollection<StatisticsItem> StatisticsItems { get; } = [];
    public ObservableCollection<DeviceStatus> DeviceStatuses { get; } = [];
    public ObservableCollection<PackageInfoItem> PackageInfoItems { get; } = [];
    public ObservableCollection<ChuteStatusItem> ChuteStatuses { get; } = [];

    // ASN相关属性
    private string _currentAsnOrderCode = "未选择";
    private string _currentCarCode = "未选择";
    private int _cachedAsnOrderCount = 0;

    public string CurrentAsnOrderCode
    {
        get => _currentAsnOrderCode;
        private set => SetProperty(ref _currentAsnOrderCode, value);
    }

    public string CurrentCarCode
    {
        get => _currentCarCode;
        private set => SetProperty(ref _currentCarCode, value);
    }

    public int CachedAsnOrderCount
    {
        get => _cachedAsnOrderCount;
        private set => SetProperty(ref _cachedAsnOrderCount, value);
    }



    /// <summary>
    /// 查找可用的格口（未分配的奇数格口）
    /// </summary>
    /// <returns>可用格口号，如果没有则返回-1</returns>
    private int FindAvailableChute()
    {
        // 按需加载配置
        var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();
        // chuteSettings.ChuteCount 表示实际物理格口总数
        // 配置格口数 = 实际格口数 / 2，例如实际6个格口对应3个配置格口
        var maxConfigChutes = Math.Max(4, chuteSettings.ChuteCount / 2);
        
        // 检查偶数格口 (2, 4, 6, 8, ...)
        for (var configChute = 1; configChute <= maxConfigChutes; configChute++)
        {
            var actualChute = 2 * configChute; // 实际格口号: 2,4,6,8...
            // 如果格口还未被分配，则可用
            if (!_skuChuteMappings.ContainsValue(actualChute))
                return actualChute;
        }

        // 没有可用的偶数格口
        return -1;
    }

    /// <summary>
    /// 为类别查找或分配格口（支持一个格口分配多个SKU）
    /// </summary>
    /// <param name="category">类别名称</param>
    /// <returns>分配的格口号，如果没有可用格口则返回-1</returns>
    private int FindOrAssignChuteForCategory(string category)
    {
        try
        {
            var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();
            
            // 首先尝试找到已有SKU数量少于2的格口
            foreach (var chute in ChuteStatuses.Where(c => c.IsAssigned && c.Categories.Count < 2))
            {
                // 分配给该格口
                _skuChuteMappings[category] = chute.ChuteNumber;
                
                // 更新格口到SKU的映射
                if (!_chuteToSkusMapping.ContainsKey(chute.ChuteNumber))
                {
                    _chuteToSkusMapping[chute.ChuteNumber] = [];
                }
                _chuteToSkusMapping[chute.ChuteNumber].Add(category);
                
                // 更新格口状态显示
                Application.Current.Dispatcher.Invoke(() =>
                {
                    chute.AssignCategory(category);
                    Log.Debug("格口状态已更新: 格口 {ChuteNumber} 新增类别 {Category}，当前类别数: {Count}", 
                        chute.ChuteNumber, category, chute.Categories.Count);
                });
                
                Log.Information("类别 {Category} 分配到现有格口 {Chute}，该格口现有 {Count} 个类别", 
                    category, chute.ChuteNumber, chute.Categories.Count);
                
                // 触发持久化保存
                SaveSkuChuteMappingToPersistence();
                
                return chute.ChuteNumber;
            }
            
            // 如果没有可以共享的格口，则查找新的可用格口
            var availableChute = FindAvailableChute();
            if (availableChute > 0)
            {
                // 分配格口给该类别
                _skuChuteMappings[category] = availableChute;
                
                // 更新格口到SKU的映射
                if (!_chuteToSkusMapping.ContainsKey(availableChute))
                {
                    _chuteToSkusMapping[availableChute] = [];
                }
                _chuteToSkusMapping[availableChute].Add(category);
                
                // 更新格口状态显示
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var chuteStatus = ChuteStatuses.FirstOrDefault(x => x.ChuteNumber == availableChute);
                    if (chuteStatus != null)
                    {
                        chuteStatus.AssignCategory(category);
                        Log.Debug("格口状态已更新: 格口 {ChuteNumber} 分配给类别 {Category}", availableChute, category);
                    }
                    else
                    {
                        Log.Warning("未找到格口 {ChuteNumber} 的状态对象", availableChute);
                    }
                });
                
                Log.Information("类别 {Category} 分配到新格口 {Chute}", category, availableChute);
                
                // 触发持久化保存
                SaveSkuChuteMappingToPersistence();
                
                return availableChute;
            }
            
            // 没有可用格口
            Log.Warning("没有可用格口分配给类别 {Category}", category);
            return -1;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "为类别 {Category} 查找或分配格口时发生错误", category);
            return -1;
        }
    }

    /// <summary>
    /// 检查条码是否为NoRead标识（空值或特殊NoRead字符串）
    /// </summary>
    /// <param name="barcode">条码</param>
    /// <returns>true表示NoRead，false表示有效条码</returns>
    private bool IsNoReadBarcode(string barcode)
    {
        // 检查空值
        if (string.IsNullOrWhiteSpace(barcode))
            return true;

        // 检查常见的NoRead标识（不区分大小写）
        var normalizedBarcode = barcode.Trim().ToUpperInvariant();
        var noReadIdentifiers = new[]
        {
            "NOREAD",
            "NO READ", 
            "NO_READ",
            "NOCODE",
            "NO CODE",
            "NO_CODE",
            "ERROR",
            "FAIL",
            "NULL"
        };

        return noReadIdentifiers.Contains(normalizedBarcode);
    }

    /// <summary>
    /// 检查SKU是否在当前选择的ASN单中
    /// </summary>
    /// <param name="sku">SKU代码</param>
    /// <returns>true表示在ASN单中，false表示不在</returns>
    private bool IsSkuInCurrentAsn(string sku)
    {
        try
        {
            // 如果没有选择ASN单，则允许所有SKU（兼容模式）
            if (string.IsNullOrEmpty(CurrentAsnOrderCode) || CurrentAsnOrderCode == "未选择")
            {
                Log.Debug("当前未选择ASN单，允许所有SKU: {Sku}", sku);
                return true;
            }

            // 从存储服务获取当前ASN单信息
            var currentAsnOrder = _asnStorageService.GetAsnOrder(CurrentAsnOrderCode);
            if (currentAsnOrder == null)
            {
                Log.Warning("无法获取当前ASN单信息: {AsnOrderCode}, 允许SKU通过", CurrentAsnOrderCode);
                return true; // 无法获取ASN信息时允许通过，避免误拦截
            }

            // 检查SKU是否在ASN单的Items中
            var skuExists = currentAsnOrder.Items.Any(item => 
                string.Equals(item.SkuCode, sku, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.ItemCode, sku, StringComparison.OrdinalIgnoreCase));

            Log.Debug("SKU {Sku} 在ASN单 {AsnOrderCode} 中的检查结果: {Exists}", 
                sku, CurrentAsnOrderCode, skuExists);

            return skuExists;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "检查SKU {Sku} 是否在ASN单 {AsnOrderCode} 中时发生错误", sku, CurrentAsnOrderCode);
            return true; // 发生错误时允许通过，避免误拦截
        }
    }

    /// <summary>
    /// 从条码中提取大区编码（第二段）
    /// 条码格式：C-A-T0123-2411080001，提取A
    /// </summary>
    /// <param name="barcode">条码</param>
    /// <returns>大区编码，如果格式不正确返回空字符串</returns>
    private string ExtractAreaCodeFromBarcode(string barcode)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(barcode))
                return string.Empty;

            var parts = barcode.Split('-');
            if (parts.Length >= 2)
            {
                var areaCode = parts[1].Trim();
                Log.Debug("从条码 {Barcode} 提取大区编码: {AreaCode}", barcode, areaCode);
                return areaCode;
            }

            Log.Warning("条码 {Barcode} 格式不符合预期，无法提取大区编码", barcode);
            return string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "提取大区编码时发生错误: {Barcode}", barcode);
            return string.Empty;
        }
    }

    /// <summary>
    /// 处理接收到的包裹信息（SKU分拣模式）
    /// </summary>
    private void OnPackageInfo(PackageInfo package)
    {
        try
        {
            var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();

            Log.Information("接收到包裹信息: {Barcode}, 重量: {Weight}kg", package.Barcode, package.Weight);

            // 记录包裹处理时间（用于效率计算）
            RecordPackageTime();

            // 更新当前条码
            CurrentBarcode = package.Barcode;

            // 更新包裹信息显示 (初始状态)
            UpdatePackageInfoItems(package);

            // 检查条码是否为空或NoRead标识
            if (IsNoReadBarcode(package.Barcode))
            {
                // 无条码，设置为NoRead异常
                package.SetStatus("无条码");
                package.ErrorMessage = "无法读取条码";
                package.SetChute(chuteSettings.NoReadChuteNumber);

                Log.Warning("包裹条码为NoRead标识 '{Barcode}'，分配到NoRead格口: {ChuteNumber}", 
                    package.Barcode, chuteSettings.NoReadChuteNumber);
                ProcessPackageWithError(package, chuteSettings.NoReadChuteNumber, "无条码");
                SavePackage(package);
                
                _totalPackageCount++;
                _failedPackageCount++;
                UpdateStatisticsItems();
                return;
            }

            // 查找或创建SKU的分拣规则
            var sku = package.Barcode; // 使用条码作为SKU
            
            // 验证SKU是否属于当前ASN单
            if (!IsSkuInCurrentAsn(sku))
            {
                // SKU不在当前ASN单中，分配到异常格口
                Log.Warning("包裹 {Barcode} 的SKU不在当前ASN单 {AsnOrderCode} 中，分配到异常格口", 
                    package.Barcode, CurrentAsnOrderCode);
                package.SetStatus("SKU不匹配");
                package.ErrorMessage = $"SKU不在ASN单 {CurrentAsnOrderCode} 中";
                package.SetChute(chuteSettings.ErrorChuteNumber);
                ProcessPackageWithError(package, chuteSettings.ErrorChuteNumber, "SKU不匹配");
                SavePackage(package);
                
                _totalPackageCount++;
                _failedPackageCount++;
                UpdateStatisticsItems();
                return;
            }
            
            // 尝试查找已有的SKU分配
            int targetChute;
            if (_skuChuteMappings.TryGetValue(sku, out var existingChute))
            {
                // SKU已有分配，使用已分配的格口
                targetChute = existingChute;
                Log.Information("包裹 {Barcode} 使用已分配格口: {Chute}", package.Barcode, targetChute);
            }
            else
            {
                // SKU还没有分配，查找或分配新格口
                targetChute = FindOrAssignChuteForCategory(sku);
                
                if (targetChute <= 0)
                {
                    // 没有可用格口，分配到异常格口
                    Log.Warning("包裹 {Barcode} 没有可用格口，分配到异常格口", package.Barcode);
                    package.SetStatus("无可用格口");
                    package.ErrorMessage = "所有格口已满，无法分配";
                    package.SetChute(chuteSettings.ErrorChuteNumber);
                    ProcessPackageWithError(package, chuteSettings.ErrorChuteNumber, "无可用格口");
                    SavePackage(package);
                    
                    _totalPackageCount++;
                    _failedPackageCount++;
                    UpdateStatisticsItems();
                    return;
                }
                
                Log.Information("包裹 {Barcode} 分配到新格口: {Chute}", package.Barcode, targetChute);
            }

            // 成功分配到格口
            package.SetChute(targetChute);
            package.SetStatus("成功");
            package.ErrorMessage = string.Empty;

            // 更新UI显示（成功状态）
            UpdatePackageInfoItemsWithCategory(sku, targetChute);

            // 分拣操作 - 成功格口
            ProcessPackageSuccess(package);

            // 记录包裹
            SavePackage(package);

            // 计数更新
            _totalPackageCount++;
            _successPackageCount++;
            UpdateStatisticsItems();

            Log.Information("包裹 {Barcode} 成功分配到格口 {ChuteNumber}，SKU: {Sku}", 
                package.Barcode, targetChute, sku);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹信息时发生错误: {Barcode}", package.Barcode);

            try
            {
                // 发生异常，分配到异常格口
                var currentChuteSettings = _settingsService.LoadSettings<ChuteSettings>();
                package.SetChute(currentChuteSettings.ErrorChuteNumber);
                package.SetStatus("处理异常");
                package.ErrorMessage = $"处理发生异常: {ex.Message}";

                ProcessPackageWithError(package, currentChuteSettings.ErrorChuteNumber, "处理异常");
                SavePackage(package);

                _totalPackageCount++;
                _failedPackageCount++;
                UpdateStatisticsItems();
            }
            catch (Exception innerEx)
            {
                Log.Error(innerEx, "处理包裹异常时发生二次错误");
            }
        }
    }

    /// <summary>
    /// 使用类别信息更新包裹显示
    /// </summary>
    private void UpdatePackageInfoItemsWithCategory(string category, int chuteNumber)
    {
        try
        {
            // 更新格口显示
            var chuteItem = PackageInfoItems.FirstOrDefault(x => x.Label == "格口");
            if (chuteItem != null)
            {
                chuteItem.Value = chuteNumber.ToString();
                chuteItem.Description = $"类别: {category}";
            }

            // 更新状态显示
            var statusItem = PackageInfoItems.FirstOrDefault(x => x.Label == "状态");
            if (statusItem != null)
                statusItem.Value = "成功";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "使用类别信息更新包裹显示时发生错误");
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

            // 计算并更新处理速率
            var currentEfficiency = CalculateCurrentEfficiency();
            var processingRateItem = StatisticsItems.FirstOrDefault(x => x.Label == "处理速率");
            if (processingRateItem != null)
                processingRateItem.Value = currentEfficiency.ToString("F1");

            // 更新峰值效率
            var peakEfficiencyItem = StatisticsItems.FirstOrDefault(x => x.Label == "峰值效率");
            if (peakEfficiencyItem != null)
                peakEfficiencyItem.Value = _peakEfficiency.ToString("F1");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新统计数据时发生错误");
        }
    }

    /// <summary>
    /// 计算当前处理效率（个/小时）
    /// </summary>
    /// <returns>当前效率值</returns>
    private double CalculateCurrentEfficiency()
    {
        lock (_efficiencyLock)
        {
            try
            {
                var now = DateTime.Now;
                
                // 如果没有处理任何包裹，返回0
                if (_totalPackageCount == 0)
                    return 0;

                // 方法1：基于会话总时间的平均效率
                var sessionDuration = now - _sessionStartTime;
                var sessionHours = sessionDuration.TotalHours;
                
                // 如果会话时间太短（少于1分钟），使用分钟级计算避免数值过大
                if (sessionHours < 1.0 / 60.0) // 少于1分钟
                {
                    var sessionMinutes = sessionDuration.TotalMinutes;
                    if (sessionMinutes > 0)
                        return (_totalPackageCount / sessionMinutes) * 60; // 转换为每小时
                    else
                        return 0;
                }

                // 方法2：基于最近时间窗口的实时效率（优先使用）
                var recentEfficiency = CalculateRecentEfficiency(now);
                if (recentEfficiency > 0)
                {
                    // 更新峰值效率
                    if (recentEfficiency > _peakEfficiency)
                        _peakEfficiency = recentEfficiency;
                    
                    return recentEfficiency;
                }

                // 方法3：回退到会话平均效率
                if (sessionHours > 0)
                {
                    var sessionAvgEfficiency = _totalPackageCount / sessionHours;
                    
                    // 更新峰值效率
                    if (sessionAvgEfficiency > _peakEfficiency)
                        _peakEfficiency = sessionAvgEfficiency;
                    
                    return sessionAvgEfficiency;
                }

                return 0;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "计算当前效率时发生错误");
                return 0;
            }
        }
    }

    /// <summary>
    /// 基于最近时间窗口计算实时效率
    /// </summary>
    /// <param name="now">当前时间</param>
    /// <returns>最近时间窗口的效率</returns>
    private double CalculateRecentEfficiency(DateTime now)
    {
        try
        {
            // 清理超过时间窗口的记录（保留最近10分钟的数据）
            var timeWindow = TimeSpan.FromMinutes(10);
            var cutoffTime = now - timeWindow;
            
            while (_recentPackageTimes.Count > 0 && _recentPackageTimes.Peek() < cutoffTime)
            {
                _recentPackageTimes.Dequeue();
            }

            // 如果最近时间窗口内的包裹数量太少，不计算实时效率
            if (_recentPackageTimes.Count < 2)
                return 0;

            // 计算最近时间窗口内的效率
            var windowDuration = now - _recentPackageTimes.Peek();
            var windowHours = windowDuration.TotalHours;
            
            if (windowHours > 0)
                return _recentPackageTimes.Count / windowHours;
            
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "计算最近效率时发生错误");
            return 0;
        }
    }

    /// <summary>
    /// 记录包裹处理时间（用于效率计算）
    /// </summary>
    private void RecordPackageTime()
    {
        lock (_efficiencyLock)
        {
            try
            {
                var now = DateTime.Now;
                _lastPackageTime = now;
                _recentPackageTimes.Enqueue(now);
                
                // 限制队列大小，避免内存占用过多（保留最近1000个记录）
                while (_recentPackageTimes.Count > 1000)
                {
                    _recentPackageTimes.Dequeue();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "记录包裹时间时发生错误");
            }
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
            
            // 历史记录中应显示配置格口号，而不是实际格口号
            // 转换公式：配置格口号 = 实际格口号 / 2
            if (record.ChuteNumber.HasValue && record.ChuteNumber.Value > 0)
            {
                record.ChuteNumber = record.ChuteNumber.Value / 2;
            }
            
            await _packageHistoryDataService.AddPackageAsync(record);
            Log.Debug("包裹记录已保存: {Barcode}, 显示格口号: {DisplayChuteNumber}", 
                package.Barcode, record.ChuteNumber);

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

    private void ExecuteOpenAsnSelection()
    {
        var parameters = new DialogParameters
        {
            { "title", "选择ASN单" }
        };

        _dialogService.ShowDialog("AsnOrderSelectionDialog", parameters, result =>
        {
            if (result.Result == ButtonResult.OK)
            {
                var selectedAsnOrder = result.Parameters.GetValue<AsnOrderInfo>("selectedAsnOrder");
                if (selectedAsnOrder != null)
                {
                    Log.Information("用户在选择对话框中选择ASN单: {OrderCode}", selectedAsnOrder.OrderCode);
                    
                    // 直接处理ASN选择
                    OnAsnOrderSelected(selectedAsnOrder);
                }
                else
                {
                    Log.Warning("从对话框结果中获取选中的ASN单失败");
                    _notificationService.ShowWarning("获取选中的ASN单失败");
                }
            }
            else
            {
                Log.Information("用户取消选择ASN单");
            }
        });
    }

    private async void ExecuteImportConfig()
    {
        try
        {
            // 打开文件选择对话框
            var openFileDialog = new OpenFileDialog
            {
                Title = "选择格口配置Excel文件",
                Filter = "Excel文件 (*.xls;*.xlsx)|*.xls;*.xlsx|所有文件 (*.*)|*.*",
                DefaultExt = ".xlsx",
                CheckFileExists = true,
                CheckPathExists = true
            };

            if (openFileDialog.ShowDialog() == true)
            {
                Log.Information("用户选择了配置文件: {FilePath}", openFileDialog.FileName);

                // 创建备份（防止导入失败时丢失原配置）
                await CreateConfigBackupAsync();

                // 导入Excel配置
                var config = await _excelImportService.ImportChuteAreaConfigAsync(openFileDialog.FileName);
                
                if (config != null && config.Items.Count > 0)
                {
                    // 清空旧的缓存和本地存储
                    ClearOldConfigData();

                    // 持久化保存新配置（增强错误处理）
                    await SaveConfigWithRetryAsync(config);

                    // 更新内存中的映射缓存
                    UpdateAreaCodeMappingCache(config);

                    Log.Information("成功导入并保存配置: {Count}个映射项", config.Items.Count);
                    _notificationService.ShowSuccess($"成功导入配置：{config.Items.Count}个映射项");

                    // 更新格口状态显示
                    UpdateChuteStatusDisplay(config);

                    // 验证配置完整性
                    await ValidateConfigIntegrityAsync(config);
                }
                else
                {
                    Log.Warning("导入配置失败或配置为空");
                    _notificationService.ShowWarning("导入配置失败，请检查文件格式");
                    
                    // 尝试恢复备份
                    await RestoreConfigBackupAsync();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导入配置时发生错误");
            _notificationService.ShowError($"导入配置失败：{ex.Message}");
            
            // 发生异常时尝试恢复备份
            await RestoreConfigBackupAsync();
        }
    }

    /// <summary>
    /// 创建配置备份
    /// </summary>
    private async Task CreateConfigBackupAsync()
    {
        try
        {
            Log.Information("开始创建配置备份");
            
            var existingConfig = _settingsService.LoadSettings<ChuteAreaConfig>();
            if (existingConfig != null && existingConfig.Items.Count > 0)
            {
                // 创建备份文件名（包含时间戳）
                var backupFileName = $"ChuteAreaConfig_backup_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                var backupDirectory = Path.Combine("Settings", "Backups");
                
                // 确保备份目录存在
                if (!Directory.Exists(backupDirectory))
                {
                    Directory.CreateDirectory(backupDirectory);
                }
                
                var backupFilePath = Path.Combine(backupDirectory, backupFileName);
                
                // 序列化并保存备份
                var json = System.Text.Json.JsonSerializer.Serialize(existingConfig, new System.Text.Json.JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
                
                await File.WriteAllTextAsync(backupFilePath, json);
                
                Log.Information("配置备份已创建: {BackupPath}", backupFilePath);
                
                // 清理旧备份（保留最近10个）
                await CleanupOldBackupsAsync(backupDirectory);
            }
            else
            {
                Log.Information("没有现有配置需要备份");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "创建配置备份时发生错误");
        }
    }

    /// <summary>
    /// 清理旧备份文件
    /// </summary>
    private async Task CleanupOldBackupsAsync(string backupDirectory)
    {
        try
        {
            await Task.Run(() =>
            {
                var backupFiles = Directory.GetFiles(backupDirectory, "ChuteAreaConfig_backup_*.json")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTime)
                    .ToList();

                // 保留最近10个备份，删除其余的
                var filesToDelete = backupFiles.Skip(10).ToList();
                foreach (var file in filesToDelete)
                {
                    try
                    {
                        file.Delete();
                        Log.Debug("已删除旧备份文件: {FileName}", file.Name);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "删除旧备份文件失败: {FileName}", file.Name);
                    }
                }

                if (filesToDelete.Count > 0)
                {
                    Log.Information("已清理 {Count} 个旧备份文件", filesToDelete.Count);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "清理旧备份文件时发生错误");
        }
    }

    /// <summary>
    /// 带重试机制的配置保存
    /// </summary>
    private async Task SaveConfigWithRetryAsync(ChuteAreaConfig config)
    {
        const int maxRetries = 3;
        const int retryDelayMs = 1000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                Log.Information("尝试保存配置，第 {Attempt}/{MaxRetries} 次", attempt, maxRetries);
                
                // 验证配置
                var validationResults = _settingsService.SaveSettings(config, validate: true, throwOnError: false);
                if (validationResults.Length > 0)
                {
                    var errors = string.Join("; ", validationResults.Select(r => r.ErrorMessage));
                    throw new InvalidOperationException($"配置验证失败: {errors}");
                }

                Log.Information("配置保存成功");
                return;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存配置失败，第 {Attempt}/{MaxRetries} 次", attempt, maxRetries);
                
                if (attempt == maxRetries)
                {
                    throw new InvalidOperationException($"保存配置失败，已重试 {maxRetries} 次: {ex.Message}", ex);
                }
                
                // 等待后重试
                await Task.Delay(retryDelayMs);
            }
        }
    }

    /// <summary>
    /// 恢复配置备份
    /// </summary>
    private async Task RestoreConfigBackupAsync()
    {
        try
        {
            Log.Information("尝试恢复配置备份");
            
            var backupDirectory = Path.Combine("Settings", "Backups");
            if (!Directory.Exists(backupDirectory))
            {
                Log.Warning("备份目录不存在，无法恢复配置");
                return;
            }

            // 查找最新的备份文件
            var latestBackup = Directory.GetFiles(backupDirectory, "ChuteAreaConfig_backup_*.json")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .FirstOrDefault();

            if (latestBackup != null)
            {
                var backupJson = await File.ReadAllTextAsync(latestBackup.FullName);
                var backupConfig = System.Text.Json.JsonSerializer.Deserialize<ChuteAreaConfig>(backupJson);
                
                if (backupConfig != null)
                {
                    // 保存恢复的配置
                    _settingsService.SaveSettings(backupConfig);
                    
                    // 更新内存缓存
                    UpdateAreaCodeMappingCache(backupConfig);
                    UpdateChuteStatusDisplay(backupConfig);
                    
                    Log.Information("已恢复配置备份: {BackupFile}", latestBackup.Name);
                    _notificationService.ShowWarning($"已恢复到备份配置：{latestBackup.CreationTime:yyyy-MM-dd HH:mm:ss}");
                }
            }
            else
            {
                Log.Warning("未找到可用的配置备份");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "恢复配置备份时发生错误");
        }
    }

    /// <summary>
    /// 验证配置完整性
    /// </summary>
    private async Task ValidateConfigIntegrityAsync(ChuteAreaConfig config)
    {
        try
        {
            await Task.Run(() =>
            {
                Log.Information("开始验证配置完整性");
                
                // 验证配置项数量
                if (config.Items.Count == 0)
                {
                    Log.Warning("配置验证: 配置项为空");
                    return;
                }
                
                // 验证格口编号范围
                var invalidChutes = config.Items.Where(item => item.ChuteNumber < 2 || item.ChuteNumber > 100).ToList();
                if (invalidChutes.Count > 0)
                {
                    Log.Warning("配置验证: 发现无效格口编号 {InvalidChutes}", 
                        string.Join(", ", invalidChutes.Select(x => x.ChuteNumber)));
                }
                
                // 验证大区编码
                var emptyAreaCodes = config.Items.Where(item => string.IsNullOrWhiteSpace(item.AreaCode)).ToList();
                if (emptyAreaCodes.Count > 0)
                {
                    Log.Warning("配置验证: 发现空的大区编码，格口编号: {EmptyAreaCodeChutes}", 
                        string.Join(", ", emptyAreaCodes.Select(x => x.ChuteNumber)));
                }
                
                // 验证重复项
                var duplicateChutes = config.Items.GroupBy(x => x.ChuteNumber).Where(g => g.Count() > 1).ToList();
                if (duplicateChutes.Count > 0)
                {
                    Log.Warning("配置验证: 发现重复格口编号 {DuplicateChutes}", 
                        string.Join(", ", duplicateChutes.Select(g => g.Key)));
                }
                
                var duplicateAreaCodes = config.Items.GroupBy(x => x.AreaCode).Where(g => g.Count() > 1).ToList();
                if (duplicateAreaCodes.Count > 0)
                {
                    Log.Warning("配置验证: 发现重复大区编码 {DuplicateAreaCodes}", 
                        string.Join(", ", duplicateAreaCodes.Select(g => g.Key)));
                }
                
                Log.Information("配置完整性验证完成");
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "验证配置完整性时发生错误");
        }
    }

    /// <summary>
    /// 清空旧的配置数据（包括文件和内存缓存）
    /// </summary>
    private void ClearOldConfigData()
    {
        try
        {
            Log.Information("开始清空旧的配置数据");

            // 清空内存中的映射缓存
            _areaCodeChuteMappings.Clear();
            _skuChuteMappings.Clear();
            _chuteToSkusMapping.Clear();

            // 清空格口状态
            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var chuteStatus in ChuteStatuses)
                {
                    chuteStatus.Clear();
                }
            });

            // 清理旧的配置文件（可选，因为新配置会覆盖旧文件）
            // 这里我们选择保留旧文件作为隐式备份，新配置会直接覆盖
            // 如果需要彻底删除旧配置文件，可以取消注释以下代码：
            /*
            try
            {
                var configFilePath = Path.Combine("Settings", "ChuteAreaConfig.json");
                if (File.Exists(configFilePath))
                {
                    File.Delete(configFilePath);
                    Log.Information("已删除旧的配置文件: {FilePath}", configFilePath);
                }
            }
            catch (Exception fileEx)
            {
                Log.Warning(fileEx, "删除旧配置文件时发生错误，但不影响新配置导入");
            }
            */

            Log.Information("旧的配置数据已清空");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "清空旧配置数据时发生错误");
        }
    }

    /// <summary>
    /// 更新大区编码映射缓存
    /// </summary>
    /// <param name="config">新的配置</param>
    private void UpdateAreaCodeMappingCache(ChuteAreaConfig config)
    {
        try
        {
            Log.Information("开始更新大区编码映射缓存");

            _areaCodeChuteMappings.Clear();
            
            foreach (var item in config.Items)
            {
                _areaCodeChuteMappings[item.AreaCode] = item.ChuteNumber;
                Log.Debug("缓存映射: 大区{AreaCode} -> 格口{ChuteNumber}", item.AreaCode, item.ChuteNumber);
            }

            Log.Information("大区编码映射缓存更新完成，共{Count}项", _areaCodeChuteMappings.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新大区编码映射缓存时发生错误");
        }
    }

    /// <summary>
    /// 更新格口状态显示
    /// </summary>
    /// <param name="config">配置</param>
    private void UpdateChuteStatusDisplay(ChuteAreaConfig config)
    {
        try
        {
            Log.Information("开始更新格口状态显示");

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var item in config.Items)
                {
                    // 配置中的格口号是实际系统格口号，需要转换为显示格口号
                    // 实际格口号 = 2 × 配置格口号 - 1，所以配置格口号 = (实际格口号 + 1) / 2
                    var displayChuteNumber = (item.ChuteNumber + 1) / 2; // 实际格口号转换为配置格口号
                    var chuteStatus = ChuteStatuses.FirstOrDefault(x => x.ChuteNumber == displayChuteNumber);
                    if (chuteStatus != null)
                    {
                        chuteStatus.AssignCategory(item.AreaCode);
                        Log.Debug("更新格口{DisplayChute}(实际格口{ActualChute})状态: 大区{AreaCode}", 
                            displayChuteNumber, item.ChuteNumber, item.AreaCode);
                    }
                    else
                    {
                        Log.Warning("未找到对应的格口状态项: 配置格口{DisplayChute}, 实际格口{ActualChute}", 
                            displayChuteNumber, item.ChuteNumber);
                    }
                }
            });

            Log.Information("格口状态显示更新完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新格口状态显示时发生错误");
        }
    }

    private void ExecuteClearChute(ChuteStatusItem? chuteStatusItem)
    {
        if (chuteStatusItem == null || !chuteStatusItem.IsAssigned)
            return;

        try
        {
            // 从映射中移除该格口的所有分配
            var categoriesToRemove = _skuChuteMappings.Where(x => x.Value == chuteStatusItem.ChuteNumber).Select(x => x.Key).ToList();
            
            foreach (var category in categoriesToRemove)
            {
                _skuChuteMappings.Remove(category);
            }
            
            // 清空格口到SKU的映射
            if (_chuteToSkusMapping.ContainsKey(chuteStatusItem.ChuteNumber))
            {
                _chuteToSkusMapping.Remove(chuteStatusItem.ChuteNumber);
            }
            
            if (categoriesToRemove.Count > 0)
            {
                Log.Information("已清空格口 {ChuteNumber} 的分配，原分配类别: {Categories}", 
                    chuteStatusItem.ChuteNumber, string.Join(", ", categoriesToRemove));
                
                // 更新UI状态
                chuteStatusItem.Clear();
                
                _notificationService.ShowSuccess($"已清空格口 {chuteStatusItem.ChuteNumber} 的分配（{categoriesToRemove.Count}个类别）");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "清空格口分配时发生错误");
            _notificationService.ShowError("清空格口分配失败：" + ex.Message);
        }
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

    private void InitializeChuteStatuses()
    {
        try
        {
            var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();
            
            // 初始化格口状态，显示配置中的原始格口号（1,2,3,4...）
            // 映射关系: 配置格口1->实际格口2, 配置格口2->实际格口4, 配置格口3->实际格口6
            // 映射公式: 实际格口 = 2 × 配置格口
            // chuteSettings.ChuteCount 表示实际物理格口总数
            // 配置格口数 = 实际格口数 / 2，例如实际6个格口对应3个配置格口
            var maxConfigChutes = Math.Max(4, chuteSettings.ChuteCount / 2); // 至少显示4个格口
            for (var configChute = 1; configChute <= maxConfigChutes; configChute++)
            {
                var actualChute = 2 * configChute; // 实际系统格口号: 2,4,6,8...
                var chuteStatus = new ChuteStatusItem(configChute, actualChute); // 显示配置格口号，存储实际格口号
                ChuteStatuses.Add(chuteStatus);
            }
            
            Log.Information("已初始化 {Count} 个配置格口状态，对应实际格口数: {ActualCount}", 
                ChuteStatuses.Count, chuteSettings.ChuteCount);
            
            // 调试：输出所有初始化的格口
            foreach (var chute in ChuteStatuses)
            {
                Log.Debug("初始化格口: 配置格口{ChuteNumber} -> 实际格口{ActualChute}, 状态: {IsAssigned}", 
                    chute.ChuteNumber, chute.ActualChuteNumber, chute.IsAssigned);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化格口状态时发生错误");
        }
    }

    private void TestAssignChute()
    {
        try
        {
            // 延迟执行，确保UI已经初始化完成
            Task.Delay(1000).ContinueWith(_ =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (ChuteStatuses.Count > 0)
                    {
                        var testChute = ChuteStatuses.First();
                        testChute.AssignCategory("A");
                        _areaCodeChuteMappings["A"] = testChute.ActualChuteNumber; // 使用实际格口号进行映射
                        
                        Log.Information("测试分配: 显示格口{DisplayChute} (实际格口{ActualChute}) 给大区 A", 
                            testChute.ChuteNumber, testChute.ActualChuteNumber);
                    }
                });
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "测试分配格口时发生错误");
        }
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
    /// 处理ASN订单接收事件（选择新ASN单时清空旧映射）
    /// </summary>
    private void OnAsnOrderSelected(AsnOrderInfo asnInfo)
    {
        try
        {
            Log.Information("处理ASN订单选择事件: {OrderCode}", asnInfo.OrderCode);

            // 检查是否是新的ASN单（与当前不同）
            var isNewAsn = CurrentAsnOrderCode != asnInfo.OrderCode;
            
            if (isNewAsn)
            {
                Log.Information("选择了新的ASN单，清空旧的SKU映射关系");
                
                // 清空内存中的映射关系
                _skuChuteMappings.Clear();
                _chuteToSkusMapping.Clear();

                // 清空持久化存储
                ClearSkuChuteMappingPersistence();

                // 清空格口状态显示
                Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var chuteStatus in ChuteStatuses)
                    {
                        chuteStatus.Clear();
                    }
                });

                Log.Information("旧的SKU映射关系已清空");
                _notificationService.ShowSuccess("已清空旧的分拣配置，准备处理新ASN单");
            }

            // 更新当前ASN信息
            CurrentAsnOrderCode = asnInfo.OrderCode;
            CurrentCarCode = asnInfo.CarCode ?? "未知";

            // 更新配置对象中的ASN信息
            _skuChuteMappingConfig.AsnOrderCode = asnInfo.OrderCode;
            _skuChuteMappingConfig.CarCode = asnInfo.CarCode ?? "未知";

            Log.Information("ASN单选择完成: {OrderCode}, 车号: {CarCode}", CurrentAsnOrderCode, CurrentCarCode);
            _notificationService.ShowSuccess($"已选择ASN单：{CurrentAsnOrderCode}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理ASN订单选择时发生错误: {OrderCode}", asnInfo.OrderCode);
            _notificationService.ShowError("处理ASN单选择失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 处理ASN单已添加到缓存事件（用于弹出选择对话框）
    /// </summary>
    private void OnAsnOrderAddedToCache(AsnOrderInfo asnInfo)
    {
        Log.Information("MainWindowViewModel收到ASN单已添加到缓存事件: {OrderCode}，准备弹出选择对话框", asnInfo.OrderCode);

        Application.Current.Dispatcher.Invoke(() =>
        {
            var parameters = new DialogParameters
            {
                { "NewAsnOrderCode", asnInfo.OrderCode } // 传递新收到的ASN单编码，用于高亮显示
            };

            _dialogService.ShowDialog("AsnOrderSelectionDialog", parameters, result =>
            {
                if (result.Result == ButtonResult.OK)
                {
                    var selectedAsnOrder = result.Parameters.GetValue<AsnOrderInfo>("selectedAsnOrder");
                    if (selectedAsnOrder != null)
                    {
                        Log.Information("用户在选择对话框中选择ASN单: {OrderCode}", selectedAsnOrder.OrderCode);
                        
                        // 发布ASN订单选择事件，由 OnAsnOrderSelected 方法处理
                        _eventAggregator.GetEvent<AsnOrderReceivedEvent>().Publish(selectedAsnOrder);
                        
                        _notificationService.ShowSuccess($"已选择ASN单：{selectedAsnOrder.OrderCode}");
                    }
                    else
                    {
                        Log.Error("从对话框结果中获取选中的ASN单失败");
                        _notificationService.ShowError("获取选中的ASN单失败");
                    }
                }
                else
                {
                    Log.Information("用户取消选择ASN单");
                    _notificationService.ShowWarning("已取消选择ASN单");
                }
            });
        });
    }

    /// <summary>
    /// 处理ASN缓存变更事件
    /// </summary>
    private void OnAsnCacheChanged(object? sender, AsnCacheChangedEventArgs e)
    {
        try
        {
            Log.Debug("ASN缓存发生变更");
            
            // 简化处理：当缓存变更时，更新UI状态
            // 具体的缓存数量通过其他方式获取
            Application.Current.Dispatcher.Invoke(() =>
            {
                // 这里可以通过其他方式获取最新的缓存数量
                // CachedAsnOrderCount = _asnCacheService.GetCount(); // 需要确认方法名
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理ASN缓存变更事件时发生错误");
        }
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
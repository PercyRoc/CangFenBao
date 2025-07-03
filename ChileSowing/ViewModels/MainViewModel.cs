using System.Collections.ObjectModel;
using System.Windows.Threading;
using Serilog;
using System.Windows.Input;
using SowingSorting.Services;
using SowingSorting.Models.Settings;
using Common.Services.Settings;
using History.Data;
using Common.Services.Ui;
using System.Diagnostics;
using Common.Models;
using ChileSowing.Services;
using Common.Events;
using WPFLocalizeExtension.Engine;
using System.Globalization;
using JetBrains.Annotations;

namespace ChileSowing.ViewModels;

public class MainViewModel : BindableBase, IDisposable
{
    private readonly IDialogService _dialogService;
    private readonly IModbusTcpService _modbusTcpService;
    private readonly ISettingsService _settingsService;
    private readonly IPackageHistoryDataService _historyService;
    private readonly INotificationService _notificationService;
    private readonly IKuaiShouApiService _kuaiShouApiService;
    private readonly IEventAggregator _eventAggregator;
    private readonly DispatcherTimer _timer;
    private bool _disposed;
    private SubscriptionToken? _chuteConfigSubscriptionToken;
    private string _currentSkuInput = string.Empty;
    private string _currentWaveInput = string.Empty;
    private string _operationStatusText = "Ready...";
    private int _packagesProcessedToday;
    private string _processingSpeed = "0 p/h";
    private SystemStatus _systemStatusObj = new();

    // 新增字段用于统计
    private int _errorCount;
    private readonly List<TimeSpan> _processingTimes = [];
    private const int MaxProcessingTimesToKeep = 100; // 存储最近100个处理时长

    // 新增字段用于顺序分配格口

    // 新增字段用于跟踪当前高亮的格口
    private ChuteViewModel? _currentlyHighlightedChute;

    // 新增字段用于存储SKU与格口的映射，以及优先格口分配的索引
    private readonly Dictionary<string, int> _skuChuteMapping = new();

    // 新增字段用于测试模式和循环分配
    private bool _isTestMode;
    private int _currentTestChuteIndex; // 测试模式下的循环分配索引
    private int _currentExceptionChuteIndex; // 异常格口的轮询索引
    
    // 测试模式下需要跳过的格口列表
    private readonly HashSet<int> _skipChuteNumbers =
    [
        1, 2, 3, 4, 33, 34, 35, 36
    ];

    // 颜色常量
    private const string DefaultChuteColor = "#FFFFFF"; // 白色
    private const string HighlightChuteColor = "#4CAF50"; // 绿色

    // 新增字段用于性能监控
    private bool _isProcessing;
    private string _lastProcessedSku = string.Empty;
    private DateTime _lastProcessTime = DateTime.MinValue;
    private readonly Queue<TimeSpan> _recentProcessingTimes = new();
    private const int MaxRecentTimesToKeep = 50;

    public MainViewModel(IDialogService dialogService, IModbusTcpService modbusTcpService, ISettingsService settingsService, IPackageHistoryDataService historyService, INotificationService notificationService, IKuaiShouApiService kuaiShouApiService, IEventAggregator eventAggregator)
    {
        _dialogService = dialogService;
        _modbusTcpService = modbusTcpService;
        _settingsService = settingsService;
        _historyService = historyService;
        _notificationService = notificationService;
        _kuaiShouApiService = kuaiShouApiService;
        _eventAggregator = eventAggregator;

        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        ViewHistoryCommand = new DelegateCommand(ExecuteViewHistory);
        ProcessSkuCommand = new DelegateCommand<string>(ExecuteProcessSku);
        ShowChutePackagesCommand = new DelegateCommand<ChuteViewModel>(ShowChutePackages);
        ToggleTestModeCommand = new DelegateCommand(ExecuteToggleTestMode);

        InitializeChutes();

        // 订阅格口配置更改事件
        _chuteConfigSubscriptionToken = _eventAggregator.GetEvent<ChuteConfigurationChangedEvent>().Subscribe(RefreshChuteConfiguration);

        // 订阅 Modbus TCP 连接状态变化事件
        _modbusTcpService.ConnectionStatusChanged += ModbusTcpService_ConnectionStatusChanged;

        // 主动查询一次连接状态，确保初始化显示正确
        ModbusTcpService_ConnectionStatusChanged(this, _modbusTcpService.IsConnected);

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        // Update command canExecute
        ViewHistoryCommand.RaiseCanExecuteChanged();
    }

    #region 属性

    public string CurrentSkuInput
    {
        get => _currentSkuInput;
        set => SetProperty(ref _currentSkuInput, value);
    }

    public string CurrentWaveInput
    {
        get => _currentWaveInput;
        set => SetProperty(ref _currentWaveInput, value);
    }

    public string OperationStatusText
    {
        get => _operationStatusText;
        set => SetProperty(ref _operationStatusText, value);
    }

    public string ProcessingSpeed
    {
        get => _processingSpeed;
        set => SetProperty(ref _processingSpeed, value);
    }

    public SystemStatus SystemStatusObj
    {
        get => _systemStatusObj;
        private set => SetProperty(ref _systemStatusObj, value);
    }

    public ObservableCollection<ChuteViewModel> Chutes { get; } = [];

    public ObservableCollection<DeviceStatus> DeviceStatuses { get; } = [];

    public ObservableCollection<PackageInfoItem> PackageInfoItems { get; } = [];
    public ObservableCollection<PackageInfoItem> StatisticsItems { get; } = [];

    public ICommand ProcessSkuCommand { [UsedImplicitly] get; }

    public ICommand ShowChutePackagesCommand { get; }

    // 新增属性
    public bool IsProcessing
    {
        get => _isProcessing;
        private set => SetProperty(ref _isProcessing, value);
    }

    public string LastProcessedSku
    {
        get => _lastProcessedSku;
        private set => SetProperty(ref _lastProcessedSku, value);
    }

    public DateTime LastProcessTime
    {
        get => _lastProcessTime;
        private set => SetProperty(ref _lastProcessTime, value);
    }

    // 成功率属性
    public double SuccessRate
    {
        get
        {
            var totalAttempts = _packagesProcessedToday + _errorCount;
            if (totalAttempts == 0) return 100.0;
            return (double)_packagesProcessedToday / totalAttempts * 100.0;
        }
    }

    // 实时处理速度 (包裹/分钟)
    public double RealtimeSpeed
    {
        get
        {
            if (_recentProcessingTimes.Count < 2) return 0;
            
            var averageTimePerPackage = _recentProcessingTimes.Average(t => t.TotalMilliseconds);
            if (averageTimePerPackage == 0) return 0;
            
            // 转换为每分钟处理的包裹数
            return 60000.0 / averageTimePerPackage;
        }
    }

    /// <summary>
    /// 是否为测试模式
    /// </summary>
    public bool IsTestMode
    {
        get => _isTestMode;
        set
        {
            if (SetProperty(ref _isTestMode, value))
            {
                OperationStatusText = value ? "已切换到测试模式（循环分配，仍写入PLC）" : "已切换到正式模式（接口分配）";
                _notificationService.ShowSuccess(value ? "已启用测试模式" : "已启用正式模式");
                Log.Information("模式切换: {Mode}", value ? "测试模式" : "正式模式");
                
                // 重置循环索引
                _currentTestChuteIndex = 0;
                _currentExceptionChuteIndex = 0;
                
                // 通知相关属性更改
                RaisePropertyChanged(nameof(CurrentModeText));
                RaisePropertyChanged(nameof(CurrentModeDescription));
            }
        }
    }

    /// <summary>
    /// 当前模式显示文本
    /// </summary>
    public string CurrentModeText
    {
        get => IsTestMode ? GetLocalizedString("Mode_Test") : GetLocalizedString("Mode_Production");
    }

    /// <summary>
    /// 当前模式描述
    /// </summary>
    public string CurrentModeDescription
    {
        get => IsTestMode
            ? GetLocalizedString("Mode_TestDescription")
            : GetLocalizedString("Mode_ProductionDescription");
    }

    #endregion

    #region 公共方法

    /// <summary>
    /// 初始化本地化内容，需要在应用完全启动后调用
    /// </summary>
    public void InitializeLocalization()
    {
        InitializePackageInfo();
        InitializeStatistics();
    }

    /// <summary>
    /// 刷新格口配置，当设置更改时调用
    /// </summary>
    private void RefreshChuteConfiguration()
    {
        var modbusSettings = _settingsService.LoadSettings<ModbusTcpSettings>();
        
        // 验证异常格口配置
        var testExceptionChute = SelectExceptionChute(modbusSettings.ExceptionChuteNumbers);
        if (testExceptionChute <= 0)
        {
            _notificationService.ShowWarning($"异常格口配置无效：{modbusSettings.ExceptionChuteNumbers}");
            Log.Warning("异常格口配置无效：{Config}", modbusSettings.ExceptionChuteNumbers);
        }
        
        InitializeChutes();
        _notificationService.ShowSuccess($"格口配置已更新：{modbusSettings.ChuteCount}个格口，异常格口：{modbusSettings.ExceptionChuteNumbers}");
        Log.Information("格口配置已更新：{ChuteCount}个格口，异常格口：{ExceptionChutes}", 
            modbusSettings.ChuteCount, modbusSettings.ExceptionChuteNumbers);
    }

    #endregion

    #region 命令

    public ICommand OpenSettingsCommand { get; }
    public DelegateCommand ViewHistoryCommand { get; }
    public ICommand ToggleTestModeCommand { [UsedImplicitly] get; }

    #endregion

    #region 命令执行方法

    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialog", new DialogParameters(), _ =>
        {
        });
        OperationStatusText = "Settings opened.";
    }

    private void ExecuteViewHistory()
    {
        var dialogParams = new DialogParameters
        {
            {
                "title", "播种墙 - 包裹历史记录"
            }
        };

        _dialogService.ShowDialog("PackageHistoryDialogView", dialogParams, result =>
        {
            if (result.Result == ButtonResult.OK)
            {
                OperationStatusText = "历史记录窗口已关闭";
            }
        });
        OperationStatusText = "历史记录窗口已打开";
    }

    private void ExecuteProcessSku(string sku)
    {
        if (string.IsNullOrEmpty(sku)) return;
        ProcessSkuInput(sku);
    }

    private void ShowChutePackages(ChuteViewModel? chute)
    {
        if (chute == null) return;
        var parameters = new DialogParameters
        {
            {
                "title", $"{GetLocalizedString("Dialog_ChutePackages_Title")} - {chute.ChuteNumber}"
            },
            {
                "skus", chute.Skus
            }
        };
        _dialogService.ShowDialog("ChuteDetailDialogView", parameters, _ => { });
    }

    private void ExecuteToggleTestMode()
    {
        IsTestMode = !IsTestMode;
    }

    #endregion

    #region 私有方法

    private async void ProcessSkuInput(string sku)
    {
        if (IsProcessing)
        {
            _notificationService.ShowWarning(GetLocalizedString("Message_PreviousRequestProcessing"));
            return;
        }

        if (string.IsNullOrWhiteSpace(sku))
        {
            _notificationService.ShowWarning(GetLocalizedString("Message_EnterValidSku"));
            return;
        }

        IsProcessing = true;
        var stopwatch = Stopwatch.StartNew(); // 开始计时

        // 获取ModbusTcp设置（在需要时重新加载以获取最新配置）
        var modbusSettings = _settingsService.LoadSettings<ModbusTcpSettings>();

        try
        {
            // 检查SKU是否已分配过格口
            var chuteNumber = 0; // 初始化为无效值

            if (IsTestMode)
            {
                // 测试模式：循环分配格口，跳过指定格口
                chuteNumber = GetNextAvailableTestChute();
                _skuChuteMapping[sku] = chuteNumber;
                Log.Information("测试模式：SKU {Sku} 循环分配到格口 {ChuteNumber}（跳过格口：1,2,3,4,33,34,35,36）",
                    sku, chuteNumber);
            }
            else if (_skuChuteMapping.TryGetValue(sku, out var assignedChute))
            {
                // 正式模式：检查是否已分配过格口
                chuteNumber = assignedChute;
                Log.Information("SKU {Sku} 已分配过格口 {ChuteNumber}，直接使用。", sku, chuteNumber);
            }
            else
            {
                // 正式模式：调用韵达接口获取格口信息
                var kuaiShouResponse = await _kuaiShouApiService.CommitScanMsgAsync(sku);
                if (kuaiShouResponse is { IsSuccess: true })
                {
                    // 解析物理格口字段，如果多个格口用|分割，取第一个
                    string physicalChute = kuaiShouResponse.Chute;
                    if (!string.IsNullOrEmpty(physicalChute))
                    {
                        var chuteNumbers = physicalChute.Split('|');
                        if (chuteNumbers.Length > 0 && int.TryParse(chuteNumbers[0].Trim(), out int parsedChute))
                        {
                            chuteNumber = parsedChute;
                            _skuChuteMapping[sku] = chuteNumber;
                            Log.Information("SKU {Sku} 韵达接口分配到格口 {ChuteNumber}，物理格口: {PhysicalChute}",
                                sku, chuteNumber, physicalChute);
                        }
                        else
                        {
                            Log.Warning("SKU {Sku} 韵达接口返回的格口号无法解析: {PhysicalChute}", sku, physicalChute);
                        }
                    }
                    else
                    {
                        Log.Warning("SKU {Sku} 韵达接口返回空的格口号", sku);
                    }
                }
                else
                {
                    Log.Warning("SKU {Sku} 韵达接口调用失败或返回错误", sku);
                }

                // 如果韵达接口未能分配格口，使用异常格口
                if (chuteNumber <= 0)
                {
                    chuteNumber = SelectExceptionChute(modbusSettings.ExceptionChuteNumbers); // 从配置的异常格口中选择一个
                    if (chuteNumber > 0)
                    {
                        _skuChuteMapping[sku] = chuteNumber;
                        OperationStatusText = $"SKU {sku} 韵达接口未能分配格口，使用异常格口 {chuteNumber}";
                        _notificationService.ShowWarning($"SKU {sku} 使用异常格口 {chuteNumber}");
                        Log.Warning("SKU {Sku} 韵达接口未能分配格口，使用异常格口 {ChuteNumber}", sku, chuteNumber);
                    }
                    else
                    {
                        OperationStatusText = $"SKU {sku} 无法分配格口：异常格口配置无效";
                        _notificationService.ShowError($"SKU {sku} 分配失败：异常格口配置无效");
                        Log.Error("SKU {Sku} 无法分配格口：异常格口配置无效 {Config}", sku, modbusSettings.ExceptionChuteNumbers);
                        stopwatch.Stop();
                        return;
                    }
                }
            }

            // 确保 chuteNumber 有效
            bool isExceptionChute = IsExceptionChute(chuteNumber, modbusSettings.ExceptionChuteNumbers);

            if (chuteNumber <= 0)
            {
                OperationStatusText = "格口分配错误：无效的格口编号。";
                _notificationService.ShowError($"格口分配失败：格口 {chuteNumber} 无效");
                stopwatch.Stop();
                return;
            }

            // 如果是异常格口，不需要检查是否在正常格口范围内
            if (!isExceptionChute && chuteNumber > Chutes.Count)
            {
                OperationStatusText = "格口分配错误：超出有效范围。";
                _notificationService.ShowError($"格口分配失败：格口 {chuteNumber} 超出有效范围 (1-{Chutes.Count})");
                stopwatch.Stop();
                return;
            }

            ChuteViewModel? selectedChute = null;
            if (isExceptionChute)
            {
                // 异常格口处理：创建临时格口或使用特殊处理逻辑
                Log.Information("使用异常格口 {ChuteNumber} 处理 SKU {Sku}", chuteNumber, sku);
                // 这里可以添加特殊的异常格口处理逻辑
                // 暂时不更新UI格口显示，只处理PLC逻辑
            }
            else
            {
                selectedChute = Chutes[chuteNumber - 1];
            }

            // 在分配新包裹前重置上一个高亮格口的颜色
            if (_currentlyHighlightedChute != null && _currentlyHighlightedChute != selectedChute)
            {
                _currentlyHighlightedChute.StatusColor = DefaultChuteColor;
            }

            // 更新UI显示
            if (PackageInfoItems.Count >= 2)
            {
                PackageInfoItems[0].Value = sku;
                PackageInfoItems[1].Value = chuteNumber.ToString();
            }

            // 写入PLC，使用格口号作为值
            int registerAddress = modbusSettings.DefaultRegisterAddress;

            // 检查PLC连接状态（测试模式和正式模式都需要PLC连接）
            if (!_modbusTcpService.IsConnected)
            {
                OperationStatusText = GetLocalizedString("Message_PlcNotConnected");
                _notificationService.ShowError(GetLocalizedString("Message_PlcNotConnected"));
                stopwatch.Stop();
                return;
            }

            // 写入PLC寄存器
            bool success = await _modbusTcpService.WriteSingleRegisterAsync(registerAddress, chuteNumber);
            stopwatch.Stop(); // 停止计时

            if (success)
            {
                // 记录PLC写入成功的日志
                string modeLog = IsTestMode ? "测试模式" : "正式模式";
                Log.Information("{Mode}：PLC写入成功，寄存器地址 {Address}，值 {Value}，SKU {Sku}",
                    modeLog, registerAddress, chuteNumber, sku);

                // 更新格口信息
                if (selectedChute != null)
                {
                    selectedChute.AddSku(sku);
                }

                string modeInfo = IsTestMode ? "（测试模式-循环分配）" : "（正式模式-接口分配）";
                OperationStatusText = $"SKU {sku} assigned to chute {chuteNumber} {modeInfo}";
                _packagesProcessedToday++; // 成功处理，总数加一

                // 记录处理时长
                _processingTimes.Add(stopwatch.Elapsed);
                if (_processingTimes.Count > MaxProcessingTimesToKeep)
                {
                    _processingTimes.RemoveAt(0);
                }

                // 更新性能监控信息
                _recentProcessingTimes.Enqueue(stopwatch.Elapsed);
                if (_recentProcessingTimes.Count > MaxRecentTimesToKeep)
                {
                    _recentProcessingTimes.Dequeue();
                }
                LastProcessedSku = sku;
                LastProcessTime = DateTime.Now;

                // 添加历史记录
                var historyRecord = new PackageHistoryRecord
                {
                    Barcode = sku,
                    ChuteNumber = chuteNumber,
                    CreateTime = DateTime.Now,
                    Status = "Sorted",
                };
                await _historyService.AddPackageAsync(historyRecord);
                Log.Information("SKU {Sku} added to history, assigned to chute {ChuteNumber}", sku, chuteNumber);

                _notificationService.ShowSuccess($"SKU {sku} assigned to chute {chuteNumber}");

                // 高亮当前分配的格口
                if (selectedChute != null)
                {
                    selectedChute.StatusColor = HighlightChuteColor;
                }
                _currentlyHighlightedChute = selectedChute;
            }
            else
            {
                stopwatch.Stop();
                string modeLog = IsTestMode ? "测试模式" : "正式模式";
                OperationStatusText = $"Failed to write to PLC for chute {chuteNumber} ({modeLog})";
                _notificationService.ShowError($"写入PLC失败，格口：{chuteNumber}");
                Log.Error("{Mode}：PLC写入失败，寄存器地址 {Address}，值 {Value}，SKU {Sku}",
                    modeLog, registerAddress, chuteNumber, sku);
                _errorCount++;
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log.Error(ex, "处理SKU输入时发生错误: {Sku}", sku);
            OperationStatusText = "Error processing SKU";
            _notificationService.ShowError("处理SKU时发生异常");
            _errorCount++;
        }
        finally
        {
            IsProcessing = false;
            CurrentSkuInput = string.Empty;
            UpdateStatistics();
        }
    }

    /// <summary>
    /// 处理 Modbus TCP 连接状态变化事件
    /// </summary>
    private void ModbusTcpService_ConnectionStatusChanged(object sender, bool isConnected)
    {
        // 在 UI 线程更新 DeviceStatuses
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var plcStatus = DeviceStatuses.FirstOrDefault(d => d.Name == "PLC");
            if (plcStatus == null) return;
            if (isConnected)
            {
                plcStatus.Status = "Connected";
                plcStatus.StatusColor = "#4CAF50"; // Green
            }
            else
            {
                plcStatus.Status = "Disconnected";
                plcStatus.StatusColor = "#F44336"; // Red
            }
        });
    }

    private void InitializeChutes()
    {
        // 从ModbusTcp设置中读取格口数量
        var modbusSettings = _settingsService.LoadSettings<ModbusTcpSettings>();
        int chuteCount = modbusSettings.ChuteCount;
        
        // 清空现有格口
        Chutes.Clear();
        
        // 根据设置生成格口
        for (int i = 1; i <= chuteCount; i++)
        {
            var chute = new ChuteViewModel(i);
            Chutes.Add(chute);
        }
        
        Log.Information("已初始化 {ChuteCount} 个格口", chuteCount);

        InitializeDeviceStatuses();
    }

    private void InitializeDeviceStatuses()
    {
        try
        {
            DeviceStatuses.Clear();
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "PLC",
                Icon = "DeveloperBoard24",
                Status = "Unknown",
                StatusColor = "#FFC107"
            });
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "KuaiShou API",
                Icon = "CloudSync24",
                Status = _kuaiShouApiService.IsEnabled ? "Unknown" : "Disabled",
                StatusColor = _kuaiShouApiService.IsEnabled ? "#FFC107" : "#9E9E9E"
            });
            
            // 测试快手API连接
            if (_kuaiShouApiService.IsEnabled)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var isConnected = await _kuaiShouApiService.TestConnectionAsync();
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            var kuaiShouStatus = DeviceStatuses.FirstOrDefault(d => d.Name == "KuaiShou API");
                            if (kuaiShouStatus != null)
                            {
                                kuaiShouStatus.Status = isConnected ? "Connected" : "Failed";
                                kuaiShouStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to test KuaiShou API connection");
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            var kuaiShouStatus = DeviceStatuses.FirstOrDefault(d => d.Name == "KuaiShou API");
                            if (kuaiShouStatus != null)
                            {
                                kuaiShouStatus.Status = "Error";
                                kuaiShouStatus.StatusColor = "#F44336";
                            }
                        });
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化设备状态列表失败。");
        }
    }

    private void InitializePackageInfo()
    {
        PackageInfoItems.Clear();
        PackageInfoItems.Add(new PackageInfoItem(
            GetLocalizedString("PackageInfo_CurrentSku"), 
            GetLocalizedString("Placeholder_NotApplicable"), 
            "", 
            GetLocalizedString("PackageInfo_CurrentSkuDesc"), 
            "BarcodeScanner24"));
        PackageInfoItems.Add(new PackageInfoItem(
            GetLocalizedString("PackageInfo_ChuteNumber"), 
            GetLocalizedString("Placeholder_NotApplicable"), 
            "", 
            GetLocalizedString("PackageInfo_ChuteNumberDesc"), 
            "BranchFork24"));
    }

    private void InitializeStatistics()
    {
        StatisticsItems.Clear();
        StatisticsItems.Add(new PackageInfoItem(
            GetLocalizedString("Statistics_TotalProcessed"), 
            "0", 
            GetLocalizedString("Unit_Pieces"), 
            GetLocalizedString("Statistics_TotalProcessedDesc"), 
            "Package24")
        {
            StatusColor = "#4CAF50"
        });
        StatisticsItems.Add(new PackageInfoItem(
            GetLocalizedString("Statistics_ErrorCount"), 
            "0", 
            GetLocalizedString("Unit_Pieces"), 
            GetLocalizedString("Statistics_ErrorCountDesc"), 
            "ErrorCircle24")
        {
            StatusColor = "#4CAF50"
        });
        StatisticsItems.Add(new PackageInfoItem(
            GetLocalizedString("Statistics_ProcessingEfficiency"), 
            "0", 
            GetLocalizedString("Unit_PiecesPerHour"), 
            GetLocalizedString("Statistics_ProcessingEfficiencyDesc"), 
            "SpeedHigh24")
        {
            StatusColor = "#4CAF50"
        });
        StatisticsItems.Add(new PackageInfoItem(
            GetLocalizedString("Statistics_AverageTime"), 
            "0", 
            GetLocalizedString("Unit_Seconds"), 
            GetLocalizedString("Statistics_AverageTimeDesc"), 
            "Timer24")
        {
            StatusColor = "#4CAF50"
        });
    }

    private void UpdateStatistics()
    {
        if (StatisticsItems.Count < 4) return;

        // 更新总包裹数
        StatisticsItems[0].Value = _packagesProcessedToday.ToString();

        // 更新异常数
        StatisticsItems[1].Value = _errorCount.ToString();

        // 计算并更新平均处理时间
        double avgTimeMs = _processingTimes.Count > 0 ? _processingTimes.Average(ts => ts.TotalMilliseconds) : 0;
        StatisticsItems[3].Value = (avgTimeMs / 1000.0).ToString("F1"); // 显示秒，保留一位小数

        // 计算并更新效率 (包裹每小时)
        // 使用总处理包裹数和应用运行时间来计算
        double totalHours = SystemStatusObj.RunningTime.TotalHours;
        double efficiency = totalHours > 0 ? _packagesProcessedToday / totalHours : 0;
        StatisticsItems[2].Value = efficiency.ToString("F0"); // 显示每小时包裹数，取整

        // 通知性能监控属性更新
        RaisePropertyChanged(nameof(SuccessRate));
        RaisePropertyChanged(nameof(RealtimeSpeed));
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        try
        {
            SystemStatusObj = SystemStatus.GetCurrentStatus();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "更新系统状态失败。");
        }
    }

    /// <summary>
    /// 获取本地化字符串
    /// </summary>
    /// <param name="key">资源键</param>
    /// <returns>本地化字符串</returns>
    private string GetLocalizedString(string key)
    {
        try
        {
            var result = LocalizeDictionary.Instance.GetLocalizedObject(
                "ChileSowing:Resources.Strings:" + key, 
                null, 
                CultureInfo.CurrentUICulture);
            return result?.ToString() ?? key;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "获取本地化字符串失败: {Key}", key);
            return key;
        }
    }

    /// <summary>
    /// 从异常格口配置中选择一个格口（均匀分配）
    /// </summary>
    /// <param name="exceptionChuteNumbers">异常格口配置字符串（分号分割）</param>
    /// <returns>选择的异常格口号，如果解析失败返回0</returns>
    private int SelectExceptionChute(string exceptionChuteNumbers)
    {
        if (string.IsNullOrWhiteSpace(exceptionChuteNumbers))
        {
            Log.Warning("异常格口配置为空");
            return 0;
        }

        var chuteNumbers = exceptionChuteNumbers.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var validChutes = new List<int>();

        foreach (var chuteStr in chuteNumbers)
        {
            if (int.TryParse(chuteStr.Trim(), out int chuteNumber) && chuteNumber > 0)
            {
                validChutes.Add(chuteNumber);
            }
            else
            {
                Log.Warning("无效的异常格口号: {ChuteString}", chuteStr);
            }
        }

        if (validChutes.Count == 0)
        {
            Log.Warning("没有找到有效的异常格口号，异常格口配置: {Config}", exceptionChuteNumbers);
            return 0;
        }

        // 如果只有一个格口，直接返回
        if (validChutes.Count == 1)
        {
            return validChutes[0];
        }

        // 多个格口时，使用轮询方式均匀分配
        var selectedChute = validChutes[_currentExceptionChuteIndex % validChutes.Count];
        _currentExceptionChuteIndex = (_currentExceptionChuteIndex + 1) % validChutes.Count;
        
        Log.Information("从 {TotalCount} 个异常格口中轮询选择了格口 {SelectedChute}（索引: {Index}）", 
            validChutes.Count, selectedChute, _currentExceptionChuteIndex);
        
        return selectedChute;
    }

    /// <summary>
    /// 检查指定格口号是否为异常格口
    /// </summary>
    /// <param name="chuteNumber">格口号</param>
    /// <param name="exceptionChuteNumbers">异常格口配置字符串</param>
    /// <returns>是否为异常格口</returns>
    private bool IsExceptionChute(int chuteNumber, string exceptionChuteNumbers)
    {
        if (string.IsNullOrWhiteSpace(exceptionChuteNumbers))
            return false;

        var chuteNumbers = exceptionChuteNumbers.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var chuteStr in chuteNumbers)
        {
            if (int.TryParse(chuteStr.Trim(), out int exceptionChute) && exceptionChute == chuteNumber)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 获取下一个可用的测试格口（跳过指定格口）
    /// </summary>
    /// <returns>下一个可用的格口号</returns>
    private int GetNextAvailableTestChute()
    {
        int totalChutes = Chutes.Count;
        int maxAttempts = totalChutes; // 最多尝试所有格口数量次，防止无限循环
        
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            _currentTestChuteIndex = (_currentTestChuteIndex + 1) % totalChutes;
            int chuteNumber = _currentTestChuteIndex + 1; // 转换为1-based索引
            
            // 检查这个格口是否在跳过列表中
            if (!_skipChuteNumbers.Contains(chuteNumber))
            {
                return chuteNumber;
            }
        }
        
        // 如果所有格口都被跳过了（理论上不会发生），返回第一个非跳过格口
        Log.Warning("测试模式：所有格口都在跳过列表中，使用备用分配逻辑");
        for (var i = 1; i <= totalChutes; i++)
        {
            if (_skipChuteNumbers.Contains(i)) continue;
            _currentTestChuteIndex = i - 1; // 更新索引
            return i;
        }
        
        // 最后的fallback，理论上不应该到达这里
        Log.Error("测试模式：无法找到可用的格口");
        return 1;
    }

    #endregion

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _timer.Stop();
            _timer.Tick -= Timer_Tick;

            // 取消订阅 Modbus TCP 连接状态变化事件
            _modbusTcpService.ConnectionStatusChanged -= ModbusTcpService_ConnectionStatusChanged;
            
            // 取消订阅格口配置更改事件
            if (_chuteConfigSubscriptionToken != null)
            {
                _eventAggregator.GetEvent<ChuteConfigurationChangedEvent>().Unsubscribe(_chuteConfigSubscriptionToken);
                _chuteConfigSubscriptionToken = null;
            }
        }
        _disposed = true;
    }
}
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
using System.Globalization;

namespace ChileSowing.ViewModels;

public class MainViewModel : BindableBase, IDisposable
{
    private readonly IDialogService _dialogService;
    private readonly IModbusTcpService _modbusTcpService;
    private readonly ISettingsService _settingsService;
    private readonly IPackageHistoryDataService _historyService;
    private readonly INotificationService _notificationService;
    private readonly ILanguageService _languageService;
    private readonly DispatcherTimer _timer;
    private bool _disposed;
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

    // 颜色常量
    private const string DefaultChuteColor = "#FFFFFF"; // 白色
    private const string HighlightChuteColor = "#4CAF50"; // 绿色

    public MainViewModel(IDialogService dialogService, IModbusTcpService modbusTcpService, ISettingsService settingsService, IPackageHistoryDataService historyService, INotificationService notificationService, ILanguageService languageService)
    {
        _dialogService = dialogService;
        _modbusTcpService = modbusTcpService;
        _settingsService = settingsService;
        _historyService = historyService;
        _notificationService = notificationService;
        _languageService = languageService;

        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        ViewHistoryCommand = new DelegateCommand(ExecuteViewHistory);
        ProcessSkuCommand = new DelegateCommand<string>(ExecuteProcessSku);
        ShowChutePackagesCommand = new DelegateCommand<ChuteViewModel>(ShowChutePackages);

        InitializeChutes();
        InitializePackageInfo();
        InitializeStatistics();

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

    public ICommand ProcessSkuCommand { get; }

    public ICommand ShowChutePackagesCommand { get; }

    #endregion

    #region 命令

    public ICommand OpenSettingsCommand { get; }
    public DelegateCommand ViewHistoryCommand { get; }

    #endregion

    #region 命令执行方法

    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialog", new DialogParameters(), _ =>
        {});
        OperationStatusText = "Settings opened.";
    }

    private void ExecuteViewHistory()
    {
        var dialogParams = new DialogParameters
        {
            { "title", "Chile Sowing Wall - Package History" }
        };

        _dialogService.ShowDialog("PackageHistoryDialogView", dialogParams, result =>
        {
            if (result.Result == ButtonResult.OK)
            {
                OperationStatusText = "History view closed.";
            }
        });
        OperationStatusText = "History view opened.";
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
            { "title", $"Chute {chute.ChuteNumber} Package List" },
            { "skus", chute.Skus }
        };
        _dialogService.ShowDialog("ChuteDetailDialogView", parameters, _ => { });
    }

    #endregion

    #region 私有方法

    private async void ProcessSkuInput(string sku)
    {
        var stopwatch = Stopwatch.StartNew(); // 开始计时
        int chuteNumber; // 初始化为无效值

        try
        {
            if (!_modbusTcpService.IsConnected)
            {
                OperationStatusText = "PLC not connected";
                _notificationService.ShowError("PLC 未连接");
                return;
            }

            // 每次用到配置都获取最新
            var chuteSettings = _settingsService.LoadSettings<Common.Models.Settings.ChuteRules.ChuteSettings>();
            if (chuteSettings == null)
            {
                OperationStatusText = "未找到格口规则配置";
                _notificationService.ShowError("未找到格口规则配置");
                return;
            }

            // 检查SKU是否已分配过格口
            if (_skuChuteMapping.TryGetValue(sku, out int assignedChute))
            {
                chuteNumber = assignedChute;
                Log.Information("SKU {Sku} 已分配过格口 {ChuteNumber}，直接使用。", sku, chuteNumber);
            }
            else
            {
                // 规则匹配分配
                var matchedChute = chuteSettings.FindMatchingChute(sku);
                if (matchedChute.HasValue)
                {
                    chuteNumber = matchedChute.Value;
                    _skuChuteMapping[sku] = chuteNumber;
                    Log.Information("SKU {Sku} 规则匹配分配到格口 {ChuteNumber}", sku, chuteNumber);
                }
                else
                {
                    // 未匹配到，优先NoReadChuteNumber，其次ErrorChuteNumber
                    chuteNumber = chuteSettings.NoReadChuteNumber > 0 ? chuteSettings.NoReadChuteNumber : chuteSettings.ErrorChuteNumber;
                    _skuChuteMapping[sku] = chuteNumber;
                    OperationStatusText = $"SKU {sku} 未匹配任何规则，分配到兜底格口 {chuteNumber}";
                    _notificationService.ShowWarning($"SKU {sku} 未匹配任何规则，分配到兜底格口 {chuteNumber}");
                    Log.Warning("SKU {Sku} 未匹配任何规则，分配到兜底格口 {ChuteNumber}", sku, chuteNumber);
                }
            }

            // 确保 chuteNumber 已经被赋值
            if (chuteNumber <= 0 || chuteNumber > Chutes.Count)
            {
                OperationStatusText = "格口分配逻辑错误。";
                _notificationService.ShowError("格口分配失败，请检查规则配置和格口数量。");
                stopwatch.Stop();
                return;
            }

            var selectedChute = Chutes[chuteNumber - 1];

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

            // 每次使用配置时都获取最新配置
            var modbusSettings = _settingsService.LoadSettings<ModbusTcpSettings>();
            int registerAddress = modbusSettings.DefaultRegisterAddress;

            // 写入PLC，使用格口号作为值
            bool success = await _modbusTcpService.WriteSingleRegisterAsync(registerAddress, chuteNumber);
            stopwatch.Stop(); // 停止计时

            if (success)
            {
                // 更新格口信息
                selectedChute.AddSku(sku);
                OperationStatusText = $"SKU {sku} assigned to chute {chuteNumber}";
                _packagesProcessedToday++; // 成功处理，总数加一

                // 记录处理时长
                _processingTimes.Add(stopwatch.Elapsed);
                if (_processingTimes.Count > MaxProcessingTimesToKeep)
                {
                    _processingTimes.RemoveAt(0);
                }

                // 添加历史记录
                var historyRecord = new PackageHistoryRecord
                {
                    Barcode = sku,
                    ChuteNumber = chuteNumber,
                    CreateTime = DateTime.Now,
                    Status = "Sorted",
                };
                await _historyService.AddPackageAsync(historyRecord);
                Log.Information("SKU {Sku} 已添加到历史记录，格口 {ChuteNumber}", sku, chuteNumber);

                _notificationService.ShowSuccess($"SKU {sku} 已分配到格口 {chuteNumber}");

                // 高亮当前分配的格口
                selectedChute.StatusColor = HighlightChuteColor;
                _currentlyHighlightedChute = selectedChute;
            }
            else
            {
                stopwatch.Stop();
                OperationStatusText = $"Failed to write to PLC for chute {chuteNumber}";
                _notificationService.ShowError($"写入PLC失败，格口：{chuteNumber}");
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
        for (int i = 1; i <= 60; i++)
        {
            var chute = new ChuteViewModel(i);
            Chutes.Add(chute);
        }

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
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化设备状态列表失败。");
        }
    }

    private void InitializePackageInfo()
    {
        PackageInfoItems.Clear();
        PackageInfoItems.Add(new PackageInfoItem("Current SKU", "N/A", "", "Currently scanned SKU", "BarcodeScanner24"));
        PackageInfoItems.Add(new PackageInfoItem("Chute Number", "N/A", "", "Current chute number", "BranchFork24"));
    }

    private void InitializeStatistics()
    {
        StatisticsItems.Clear();
        StatisticsItems.Add(new PackageInfoItem("Total Packages", "0", "pcs", "Total packages processed", "Package24") { StatusColor = "#4CAF50" });
        StatisticsItems.Add(new PackageInfoItem("Error Count", "0", "pcs", "Total error packages", "ErrorCircle24") { StatusColor = "#4CAF50" });
        StatisticsItems.Add(new PackageInfoItem("Efficiency", "0", "p/h", "Processing efficiency", "SpeedHigh24") { StatusColor = "#4CAF50" });
        StatisticsItems.Add(new PackageInfoItem("Avg. Time", "0", "ms", "Average processing time", "Timer24") { StatusColor = "#4CAF50" });
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
        }
        _disposed = true;
    }
}
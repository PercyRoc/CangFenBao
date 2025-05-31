using System.Collections.ObjectModel;
using System.Windows.Threading;
using Common.Models.Package;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera.TCP;
using Serilog;
using SortingServices.Modules;
using Application = System.Windows.Application;
using Microsoft.Win32;
using System.IO;
using System.Text;
using Camera.Services.Implementations.TCP;
using Common.Models;
using WeiCiModule.Models;

namespace WeiCiModule.ViewModels;

public class MainViewModel : BindableBase, IDisposable
{
    private readonly IDialogService _dialogService;
    private readonly TcpCameraService _cameraService;
    private readonly IModuleConnectionService _moduleConnectionService;
    private readonly ISettingsService _settingsService;
    private readonly DispatcherTimer _timer;
    private string _currentBarcode = string.Empty;
    private SystemStatus _systemStatus = new();
    private string _searchImportedText = string.Empty;
    private readonly IDisposable? _packageStreamSubscription;
    private int _nextChuteNumber = 1; // 修改：用于1-32格口循环

    public ObservableCollection<PackageInfo> PackageHistory { get; } = [];
    public ObservableCollection<StatisticsItem> StatisticsItems { get; } = [];
    public ObservableCollection<DeviceStatus> DeviceStatuses { get; } = [];
    public ObservableCollection<PackageInfoItem> PackageInfoItems { get; } = [];
    public ObservableCollection<BarcodeChuteMapping> AllImportedMappings { get; } = [];
    public ObservableCollection<BarcodeChuteMapping> FilteredImportedMappings { get; } = [];
    
    public MainViewModel(IDialogService dialogService, ISettingsService settingsService,
        IModuleConnectionService moduleConnectionService, TcpCameraService cameraService)
    {
        _dialogService = dialogService;
        _settingsService = settingsService;
        _moduleConnectionService = moduleConnectionService;
        _cameraService = cameraService;
        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        ImportConfigCommand = new DelegateCommand(ExecuteImportConfig);
        SearchImportedCommand = new DelegateCommand(ExecuteSearchImported);

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

        // 订阅模组带连接状态事件
        _moduleConnectionService.ConnectionStateChanged += OnModuleConnectionChanged;
        
        // 订阅相机服务事件
        _cameraService.ConnectionChanged += OnCameraConnectionChanged;
        _packageStreamSubscription = _cameraService.PackageStream.Subscribe(OnCameraPackageStreamReceived);

        // 启动时加载已保存的导入映射
        _ = LoadInitialImportedMappingsAsync();
    }
    
    public DelegateCommand OpenSettingsCommand { get; }
    public DelegateCommand ImportConfigCommand { get; }
    public DelegateCommand SearchImportedCommand { get; }
    public SystemStatus SystemStatus
    {
        get => _systemStatus;
        private set => SetProperty(ref _systemStatus, value);
    }

    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialog");
    }

    private async void ExecuteImportConfig()
    {
        var openFileDialog = new OpenFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "选择要导入的配置文件"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            try
            {
                var filePath = openFileDialog.FileName;
                Log.Information("开始导入配置文件: {FilePath}", filePath);

                var newMappings = new List<BarcodeChuteMapping>();

                string[] lines = await File.ReadAllLinesAsync(filePath, Encoding.UTF8);

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Split([','], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        string barcodePart = parts[0].Trim();
                        string chutePart = parts[1].Trim();

                        // 检查条码和格口部分是否都以^开头和结尾
                        if (barcodePart.StartsWith('^') && barcodePart.EndsWith('^') &&
                            chutePart.StartsWith('^') && chutePart.EndsWith('^'))
                        {
                            // 提取实际的条码和格口名称
                            // 根据最新描述，A或B是内容的一部分，不是^B这样的特殊标记
                            string barcode = barcodePart.Substring(1, barcodePart.Length - 2);
                            string chute = chutePart.Substring(1, chutePart.Length - 2);

                            if (!string.IsNullOrWhiteSpace(barcode) && !string.IsNullOrWhiteSpace(chute))
                            {
                                newMappings.Add(new BarcodeChuteMapping { Barcode = barcode, Chute = chute });
                            }
                            else
                            {
                                Log.Warning("解析到无效的条码或格口数据，行: {LineData}", line);
                            }
                        }
                        else
                        {
                            Log.Warning("行数据格式不符合预期 (缺少^B或^)，行: {LineData}", line);
                        }
                    }
                    else
                    {
                        Log.Warning("行数据字段不足，行: {LineData}", line);
                    }
                }

                if (newMappings.Count != 0)
                {
                    var settingsToSave = new BarcodeChuteMappingSettings { Mappings = newMappings };
                    _settingsService.SaveSettings(settingsToSave, true);
                    Log.Information("成功导入并保存 {Count} 条条码格口映射配置。", newMappings.Count);
                    HandyControl.Controls.Growl.Success($"Successfully imported {newMappings.Count} mapping configurations!");

                    // 更新导入数据显示的集合
                    AllImportedMappings.Clear();
                    FilteredImportedMappings.Clear();
                    foreach (var mapping in newMappings)
                    {
                        AllImportedMappings.Add(mapping);
                    }
                    ExecuteSearchImported();
                }
                else
                {
                    Log.Information("导入的文件中未找到有效的条码格口映射数据。请确保数据格式为 ^Barcode^,^Chute^ 。");
                    HandyControl.Controls.Growl.Warning("No valid mapping data found. Ensure format is ^Barcode^,^Chute^.");
                    AllImportedMappings.Clear();
                    FilteredImportedMappings.Clear();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "导入配置文件失败。");
                HandyControl.Controls.Growl.Error($"Failed to import configuration file: {ex.Message}");
            }
        }
        else
        {
            Log.Information("用户取消了导入操作。");
        }
    }
    
    private void Timer_Tick(object? sender, EventArgs e)
    {
        SystemStatus = SystemStatus.GetCurrentStatus();
    }
    
    public string CurrentBarcode
    {
        get => _currentBarcode;
        private set => SetProperty(ref _currentBarcode, value);
    }

    public string SearchImportedText
    {
        get => _searchImportedText;
        set
        {
            if (SetProperty(ref _searchImportedText, value))
            {
                ExecuteSearchImported();
            }
        }
    }

    private void ExecuteSearchImported()
    {
        FilteredImportedMappings.Clear();
        if (string.IsNullOrWhiteSpace(SearchImportedText))
        {
            foreach (var mapping in AllImportedMappings)
            {
                FilteredImportedMappings.Add(mapping);
            }
        }
        else
        {
            string searchText = SearchImportedText.Trim().ToLowerInvariant();
            foreach (var mapping in AllImportedMappings)
            {
                if ((mapping.Barcode.ToLowerInvariant().Contains(searchText, StringComparison.InvariantCultureIgnoreCase)) ||
                    (mapping.Chute.ToLowerInvariant().Contains(searchText, StringComparison.InvariantCultureIgnoreCase)))
                {
                    FilteredImportedMappings.Add(mapping);
                }
            }
        }
    }
    
    private void InitializeDeviceStatuses()
    {
        try
        {
            Log.Debug("开始初始化设备状态列表");

            // 添加相机状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "Camera Service", // "相机"
                Status = "Disconnected", // "未连接"
                Icon = "Camera24",
                StatusColor = "#F44336"
            });

            // 添加模组带状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "Module Belt", // "模组带"
                Status = "Disconnected", // "未连接"
                Icon = "ArrowSort24",
                StatusColor = "#F44336" // 红色表示未连接
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化设备状态列表时发生错误");
        }
    }
    
    private void InitializeStatisticsItems()
    {
        StatisticsItems.Add(new StatisticsItem(
            "Total Packages", // "总包裹数"
            "0",
            "pcs", // "个"
            "Total number of packages processed", // "累计处理包裹总数"
            "BoxMultiple24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "Successes", // "成功数"
            "0",
            "pcs", // "个"
            "Number of successfully processed packages", // "处理成功的包裹数量"
            "CheckmarkCircle24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "Failures", // "失败数"
            "0",
            "pcs", // "个"
            "Number of failed packages", // "处理失败的包裹数量"
            "ErrorCircle24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "Processing Rate", // "处理速率"
            "0",
            "pcs/hr", // "个/小时"
            "Packages processed per hour", // "每小时处理包裹数量"
            "ArrowTrendingLines24"
        ));
    }
    
    private void InitializePackageInfoItems()
    {
        PackageInfoItems.Add(new PackageInfoItem(
            "Chute", // "格口"
            "N/A",
            "",
            "Target chute number", // "目标格口号"
            "DoorArrowRight24" // 使用一个更合适的图标，如果可用
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "Time", // "时间"
            "--:--:--",
            "",
            "Processing time", // "处理时间"
            "Timer24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "Status", // "状态"
            "Waiting", // "等待"
            "",
            "Processing status", // "处理状态"
            "AlertCircle24"
        ));
    }
    
    private void OnModuleConnectionChanged(object? sender, bool isConnected)
    {
        try
        {
            var moduleStatus = DeviceStatuses.FirstOrDefault(static x => x.Name == "Module Belt");
            if (moduleStatus == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                moduleStatus.Status = isConnected ? "Connected" : "Disconnected"; // "已连接" : "已断开"
                moduleStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新模组带状态时发生错误");
        }
    }
    
    private void OnCameraConnectionChanged(string? cameraId, bool isConnected)
    {
        try
        {
            var cameraStatus = DeviceStatuses.FirstOrDefault(static x => x.Name == "Camera Service");
            if (cameraStatus == null) return;

            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                cameraStatus.Status = isConnected ? "Connected" : "Disconnected"; // "已连接" : "已断开"
                cameraStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
                Log.Information("相机(ID: {CameraId}) 连接状态变更: {IsConnected}", cameraId ?? "N/A", isConnected);
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新相机连接状态时发生错误");
        }
    }

    private async void OnCameraPackageStreamReceived(PackageInfo packageInfo)
    {
        // Step 1: Immediate UI Update with available data (on dispatcher)
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            CurrentBarcode = packageInfo.Barcode;

            if (packageInfo.Barcode == "NOREAD" || packageInfo.Status == PackageStatus.NoRead)
            {
                UpdatePackageInfoItems_NoRead(packageInfo.TriggerTimestamp);
            }
            else
            {
                UpdatePackageInfoItems_Raw(packageInfo);
            }
        });
        string? operationMessage;

        try
        {
            if (packageInfo.Barcode == "NOREAD" || packageInfo.Status == PackageStatus.NoRead)
            {
                Log.Information("开始处理相机无码事件，条码标记为: {Barcode}", packageInfo.Barcode);
                var moduleTcpSettingsNoRead = _settingsService.LoadSettings<SortingServices.Modules.Models.ModelsTcpSettings>();
                string exceptionChuteStringNoRead = moduleTcpSettingsNoRead.ExceptionChute.ToString();

                if (int.TryParse(exceptionChuteStringNoRead, out var chuteIntNoRead))
                {
                    packageInfo.SetChute(chuteIntNoRead);
                }
                else
                {
                    Log.Warning("无法将NoRead的异常格口 '{ExceptionChute}' 解析为整数", exceptionChuteStringNoRead);
                    packageInfo.SetChute(998);
                }
                packageInfo.SetStatus(PackageStatus.NoRead, "NoRead, to exception chute"); // Ensure status is set
                operationMessage = $"无码事件，分配到异常格口 {packageInfo.ChuteNumber}.";
                Log.Information(operationMessage);
            }
            else // Regular package with barcode
            {
                Log.Information("开始处理相机包裹数据事件: {Barcode}", packageInfo.Barcode);

                var mappingSettings = _settingsService.LoadSettings<BarcodeChuteMappingSettings>();
                var moduleTcpSettings = _settingsService.LoadSettings<SortingServices.Modules.Models.ModelsTcpSettings>();
                string exceptionChute = moduleTcpSettings.ExceptionChute.ToString();

                var mapping = mappingSettings.Mappings.FirstOrDefault(m => m.Barcode == packageInfo.Barcode);

                if (mapping != null)
                {
                    string mappedBranchName = mapping.Chute;
                    Log.Information("条码 {Barcode} 查找到映射规则，目标库分馆名称: {BranchName}", packageInfo.Barcode, mappedBranchName);

                    var chuteSettingsConfig = _settingsService.LoadSettings<Models.Settings.ChuteSettings>();
                    var chuteData = chuteSettingsConfig.Items.FirstOrDefault(csd =>
                        string.Equals(csd.Branch.Trim(), mappedBranchName.Trim(), StringComparison.OrdinalIgnoreCase));

                    if (chuteData != null)
                    {
                        string targetChuteCode = chuteData.BranchCode;
                        Log.Information("库分馆名称 '{BranchName}' 在 ChuteSettings 中找到对应记录，库分馆代码 (格口号): '{BranchCode}'", mappedBranchName, targetChuteCode);

                        if (int.TryParse(targetChuteCode, out var chuteInt))
                        {
                            packageInfo.SetChute(chuteInt);
                            packageInfo.SetStatus(PackageStatus.Success, $"Chute: {chuteInt} (From: {mappedBranchName})");
                            operationMessage = $"条码 {packageInfo.Barcode} -> 库分馆 '{mappedBranchName}' -> 代码 '{targetChuteCode}' -> 格口 {packageInfo.ChuteNumber}.";
                            Log.Information(operationMessage);
                        }
                        else
                        {
                            Log.Warning("无法将库分馆代码 '{BranchCode}' (来自库分馆 '{BranchName}', 条码 '{Barcode}') 解析为整数格口. 将分配到异常口.", targetChuteCode, mappedBranchName, packageInfo.Barcode);
                            packageInfo.SetChute(int.TryParse(exceptionChute, out var exChuteInt) ? exChuteInt : 999);
                            packageInfo.SetStatus(PackageStatus.Error, $"Code '{targetChuteCode}' parsing failed, to exception chute");
                            operationMessage = $"条码 {packageInfo.Barcode} (库分馆 '{mappedBranchName}') 的格口代码 '{targetChuteCode}' 解析失败, 分配到异常格口 {packageInfo.ChuteNumber}.";
                            Log.Warning(operationMessage);
                        }
                    }
                    else
                    {
                        Log.Warning("在 ChuteSettings 中未找到条码 '{Barcode}' 映射的库分馆名称 '{BranchName}'. 将分配到异常口.", packageInfo.Barcode, mappedBranchName);
                        packageInfo.SetChute(int.TryParse(exceptionChute, out var exChuteInt) ? exChuteInt : 999);
                        packageInfo.SetStatus(PackageStatus.Error, $"Branch '{mappedBranchName}' not configured, to exception chute");
                        operationMessage = $"条码 {packageInfo.Barcode} 映射的库分馆名称 '{mappedBranchName}' 未在 ChuteSettings 中找到, 分配到异常格口 {packageInfo.ChuteNumber}.";
                        Log.Warning(operationMessage);
                    }
                }
                else
                {
                    if (int.TryParse(exceptionChute, out var chuteInt))
                    {
                        packageInfo.SetChute(chuteInt);
                    }
                    else
                    {
                        Log.Warning("无法将异常格口 '{ExceptionChute}' 解析为整数，条码: {Barcode}", exceptionChute, packageInfo.Barcode);
                        packageInfo.SetChute(999); // Default exception chute
                    }
                    packageInfo.SetStatus(PackageStatus.Error, "No rule found, to exception chute");
                    operationMessage = $"条码 {packageInfo.Barcode} 未找到映射规则，分配到异常格口 {packageInfo.ChuteNumber}.";
                    Log.Warning(operationMessage);
                }
            }
            
            // 修改：格口从1依次递增到32
            packageInfo.SetChute(_nextChuteNumber);
            Log.Information("循环格口逻辑：设置包裹 GUID:{Guid}, 条码:{Barcode} 到格口 {Chute}", packageInfo.Guid, packageInfo.Barcode, _nextChuteNumber);

            _nextChuteNumber++;
            if (_nextChuteNumber > 32)
            {
                _nextChuteNumber = 1; // 循环回1
            }

            // Step 3: Send to module service
            _moduleConnectionService.OnPackageReceived(packageInfo);

            // Step 4: Final UI Update with processed package data (on dispatcher)
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                UpdatePackageInfoItems_Final(packageInfo);

                PackageHistory.Insert(0, packageInfo);
                if (PackageHistory.Count > 1000)
                {
                    PackageHistory.RemoveAt(PackageHistory.Count - 1);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理相机包裹流事件的业务逻辑时发生错误。");
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                HandyControl.Controls.Growl.Error("Error processing package stream event, please check logs.");
                UpdatePackageInfoItems_Reset();
                CurrentBarcode = string.Empty;
            });
        }
    }

    // Helper methods for updating PackageInfoItems
    private void UpdatePackageInfoItems_Raw(PackageInfo rawPackage)
    {
        var chuteItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Chute");
        if (chuteItem != null) chuteItem.Value = "Processing..."; // Or some initial value before chute is determined

        var timeItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Time");
        // Use CreateTime from package if available and valid, otherwise default to Now for display purposes during raw update
        if (timeItem != null) timeItem.Value = (rawPackage.CreateTime == DateTime.MinValue ? DateTime.Now : rawPackage.CreateTime).ToString("HH:mm:ss");


        var statusItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Status");
        if (statusItem != null)
        {
            statusItem.Value = "Processing...";
            statusItem.Icon = "Hourglass24";
        }
    }

    private void UpdatePackageInfoItems_NoRead(DateTime eventTime)
    {
        var chuteItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Chute");
        if (chuteItem != null) chuteItem.Value = "N/A"; // Or the specific exception chute if known at this stage

        var timeItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Time");
        if (timeItem != null) timeItem.Value = eventTime.ToString("HH:mm:ss");

        var statusItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Status");
        if (statusItem != null)
        {
            statusItem.Value = "No Read";
            statusItem.Icon = "Warning24";
        }
    }

    private void UpdatePackageInfoItems_Reset()
    {
        var chuteItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Chute");
        if (chuteItem != null) chuteItem.Value = "N/A";

        var timeItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Time");
        if (timeItem != null) timeItem.Value = "--:--:--";

        var statusItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Status");
        if (statusItem != null)
        {
            statusItem.Value = "Waiting";
            statusItem.Icon = "AlertCircle24";
        }
    }

    private void UpdatePackageInfoItems_Final(PackageInfo processedPackage)
    {
        var chuteItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Chute");
        if (chuteItem != null) chuteItem.Value = processedPackage.ChuteNumber.ToString();

        var timeItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Time");
        if (timeItem != null) timeItem.Value = processedPackage.CreateTime.ToString("HH:mm:ss");

        var statusItem = PackageInfoItems.FirstOrDefault(i => i.Label == "Status");
        if (statusItem != null)
        {
            statusItem.Value = processedPackage.StatusDisplay;
            statusItem.Icon = processedPackage.Status switch
            {
                PackageStatus.Success => "CheckmarkCircle24",
                PackageStatus.Error => "ErrorCircle24",
                PackageStatus.NoRead => "Warning24",
                _ => "AlertCircle24"
            };
        }
    }

    private async Task LoadInitialImportedMappingsAsync()
    {
        try
        {
            Log.Information("尝试在启动时异步加载已保存的条码格口映射配置。");
            // 模拟异步加载，如果 _settingsService.LoadSettings 本身不是异步的
            // 在真实场景中，如果 LoadSettings 是CPU密集型或IO密集型，应考虑将其封装为真正的异步操作
            var savedMappingsSettings = await Task.Run(() => _settingsService.LoadSettings<BarcodeChuteMappingSettings>());

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (savedMappingsSettings.Mappings.Count > 0)
                {
                    AllImportedMappings.Clear();
                    FilteredImportedMappings.Clear(); // 确保在添加前清空
                    foreach (var mapping in savedMappingsSettings.Mappings)
                    {
                        AllImportedMappings.Add(mapping);
                    }
                    ExecuteSearchImported(); // 使用加载的数据填充过滤后的列表
                    Log.Information("成功异步加载并显示了 {Count} 条已保存的条码格口映射配置。", AllImportedMappings.Count);
                }
                else
                {
                    Log.Information("未找到或无有效的已保存条码格口映射配置 (异步加载)。");
                    AllImportedMappings.Clear(); // 确保集合为空
                    FilteredImportedMappings.Clear();
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "在启动时异步加载已保存的条码格口映射配置失败。");
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                AllImportedMappings.Clear(); // 出错时也确保集合为空
                FilteredImportedMappings.Clear();
            });
        }
    }

    public void Dispose()
    {
        try
        {
            _timer.Stop();
            _timer.Tick -= Timer_Tick;

            _moduleConnectionService.ConnectionStateChanged -= OnModuleConnectionChanged;
            
            // Unsubscribe from ICameraService events
            _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
            _packageStreamSubscription?.Dispose();

            // Remove old unsubscribes
            // _hikvisionDwsCommunicatorService.ConnectionStatusChanged -= OnHikvisionDwsConnectionStatusChanged;
            // _hikvisionDwsCommunicatorService.PackageDataReceived -= OnDwsPackageOrNoReadEventAsync;
            // _hikvisionDwsCommunicatorService.NoReadEventOccurred -= OnDwsPackageOrNoReadEventAsync;
          
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放资源时发生错误");
        }
        GC.SuppressFinalize(this);
    }
}
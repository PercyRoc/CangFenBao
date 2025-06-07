using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Common.Models.Package;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.Camera;
using DeviceService.DataSourceDevices.Services;
using Serilog;
using ShanghaiModuleBelt.Models.Sto;
using ShanghaiModuleBelt.Models.Sto.Settings;
using ShanghaiModuleBelt.Models.Yunda;
using ShanghaiModuleBelt.Services;
using ShanghaiModuleBelt.Services.Sto;
using ShanghaiModuleBelt.Services.Yunda;
using SharedUI.Models;
using Modules.Services.Jitu;
// using LockingService = ShanghaiModuleBelt.Services.LockingService;

namespace ShanghaiModuleBelt.ViewModels;

internal class MainWindowViewModel : BindableBase, IDisposable
{
    private readonly ICameraService _cameraService;

    // 格口锁定状态字典
    // private readonly ChuteMappingService _chuteMappingService;
    private readonly IDialogService _dialogService;
    // private readonly LockingService _lockingService;
    private readonly IModuleConnectionService _moduleConnectionService;
    private readonly ISettingsService _settingsService;
    private readonly IStoAutoReceiveService _stoAutoReceiveService;
    private readonly IYundaUploadWeightService _yundaUploadWeightService;
    private readonly Services.Zto.IZtoApiService _ztoApiService;
    private readonly IJituService _jituService;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _timer;
    private string _currentBarcode = string.Empty;
    private bool _disposed;
    private SystemStatus _systemStatus = new();

    // 格口统计计数器 - 维护完整的统计数据
    private readonly Dictionary<int, int> _chutePackageCount = new();

    public MainWindowViewModel(IDialogService dialogService,
        ICameraService cameraService,
        PackageTransferService packageTransferService, ISettingsService settingsService,
        IModuleConnectionService moduleConnectionService,
        IStoAutoReceiveService stoAutoReceiveService,
        IYundaUploadWeightService yundaUploadWeightService,
        Services.Zto.IZtoApiService ztoApiService,
        IJituService jituService)
    {
        _dialogService = dialogService;
        _cameraService = cameraService;
        _settingsService = settingsService;
        _moduleConnectionService = moduleConnectionService;
        // _chuteMappingService = chuteMappingService;
        // _lockingService = lockingService;
        _stoAutoReceiveService = stoAutoReceiveService;
        _yundaUploadWeightService = yundaUploadWeightService;
        _ztoApiService = ztoApiService;
        _jituService = jituService;
        OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
        ShowChuteStatisticsCommand = new DelegateCommand(ExecuteShowChuteStatistics);

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

        // 订阅模组带连接状态事件
        _moduleConnectionService.ConnectionStateChanged += OnModuleConnectionChanged;

        // // 订阅锁格状态变更事件
        // _lockingService.ChuteLockStatusChanged += OnChuteLockStatusChanged;
        //
        // // 订阅锁格设备连接状态变更事件
        // _lockingService.ConnectionStatusChanged += OnLockingDeviceConnectionChanged;
        // 订阅包裹流
        _subscriptions.Add(packageTransferService.PackageStream
            .Subscribe(package => { Application.Current.Dispatcher.BeginInvoke(() => OnPackageInfo(package)); }));

        // 初始检查锁格设备状态
        // UpdateLockingDeviceStatus(_lockingService.IsConnected());
    }
    
    public DelegateCommand OpenSettingsCommand { get; }
    public DelegateCommand ShowChuteStatisticsCommand { get; }
    public string CurrentBarcode
    {
        get => _currentBarcode;
        private set => SetProperty(ref _currentBarcode, value);
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

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void ExecuteOpenSettings()
    {
        _dialogService.ShowDialog("SettingsDialog");
    }

    private void ExecuteShowChuteStatistics()
    {
        try
        {
            var dialog = new Views.ChuteStatisticsDialog();
            var viewModel = new ChuteStatisticsDialogViewModel();
            
            // 更新统计数据
            viewModel.UpdateStatistics(_chutePackageCount);
            
            // 设置数据上下文
            dialog.DataContext = viewModel;
            
            // 设置刷新动作的处理逻辑
            viewModel.RefreshAction = () =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    viewModel.UpdateStatistics(_chutePackageCount);
                });
            };
            
            // 设置父窗口并显示对话框
            dialog.Owner = Application.Current.MainWindow;
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "显示格口统计对话框时发生错误");
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

            // 添加模组带状态
            DeviceStatuses.Add(new DeviceStatus
            {
                Name = "模组带",
                Status = "未连接",
                Icon = "ArrowSort24",
                StatusColor = "#F44336" // 红色表示未连接
            });
            //
            // // 添加锁格设备状态
            // DeviceStatuses.Add(new DeviceStatus
            // {
            //     Name = "锁格设备",
            //     Status = "未连接",
            //     Icon = "Lock24",
            //     StatusColor = "#F44336" // 红色表示未连接
            // });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化设备状态列表时发生错误");
        }
    }

    private void InitializeStatisticsItems()
    {
        StatisticsItems.Add(new StatisticsItem(
            "总包裹数",
            "0",
            "个",
            "累计处理包裹总数",
            "BoxMultiple24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "成功数",
            "0",
            "个",
            "处理成功的包裹数量",
            "CheckmarkCircle24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "失败数",
            "0",
            "个",
            "处理失败的包裹数量",
            "ErrorCircle24"
        ));

        StatisticsItems.Add(new StatisticsItem(
            "处理速率",
            "0",
            "个/小时",
            "每小时处理包裹数量",
            "ArrowTrendingLines24"
        ));
    }

    private void InitializePackageInfoItems()
    {
        PackageInfoItems.Add(new PackageInfoItem(
            "重量",
            "0.00",
            "kg",
            "包裹重量",
            "Scales24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "尺寸",
            "0 × 0 × 0",
            "mm",
            "长 × 宽 × 高",
            "Ruler24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "时间",
            "--:--:--",
            "",
            "处理时间",
            "Timer24"
        ));

        PackageInfoItems.Add(new PackageInfoItem(
            "状态",
            "等待",
            "",
            "处理状态",
            "AlertCircle24"
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

    private void OnModuleConnectionChanged(object? sender, bool isConnected)
    {
        try
        {
            var moduleStatus = DeviceStatuses.FirstOrDefault(static x => x.Name == "模组带");
            if (moduleStatus == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                moduleStatus.Status = isConnected ? "已连接" : "已断开";
                moduleStatus.StatusColor = isConnected ? "#4CAF50" : "#F44336";
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新模组带状态时发生错误");
        }
    }

    private async void OnPackageInfo(PackageInfo package)
    {
        try
        {
            Log.Information("收到包裹信息: {Barcode}, 序号={Index}", package.Barcode, package.Index);

            // 从 ChuteSettings 获取格口配置
            var chuteSettings = _settingsService.LoadSettings<Common.Models.Settings.ChuteRules.ChuteSettings>();

            // 检查条码是否包含异常字符（|或逗号）
            if (package.Barcode.Contains('|') || package.Barcode.Contains(','))
            {
                Log.Warning("条码包含异常字符（|或,），分到异常格口: {Barcode}", package.Barcode);
                package.SetChute(chuteSettings.ErrorChuteNumber);
                package.SetStatus(PackageStatus.Error, "条码包含异常字符");
            }
            else
            {
                // 根据条码查找匹配的格口
                var chuteNumber = chuteSettings.FindMatchingChute(package.Barcode);

                if (chuteNumber == null)
                {
                    Log.Warning("无法获取格口号，使用异常格口: {Barcode}", package.Barcode);
                    package.SetChute(chuteSettings.ErrorChuteNumber); // 使用 ChuteSettings 中的异常格口
                    package.SetStatus(PackageStatus.Error, "格口分配失败");
                }
                else
                {
                    // 设置包裹的格口
                    package.SetChute(chuteNumber.Value); // 不再有原始格口的概念，因为直接匹配规则
                }
            }

            // 通知模组带服务处理包裹
            _moduleConnectionService.OnPackageReceived(package);

            // 更新格口统计计数器
            if (_chutePackageCount.ContainsKey(package.ChuteNumber))
            {
                _chutePackageCount[package.ChuteNumber]++;
            }
            else
            {
                _chutePackageCount[package.ChuteNumber] = 1;
            }

            // 如果没有错误，设置为正常状态
            if (string.IsNullOrEmpty(package.ErrorMessage))
            {
                package.SetStatus(PackageStatus.Success, "正常");
            }

            // 获取申通API配置
            var stoApiSettings = _settingsService.LoadSettings<StoApiSettings>();
            var stoPrefixes = stoApiSettings.BarcodePrefixes.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();

            // 根据配置的条码前缀上传申通自动揽收
            if (stoPrefixes.Any(prefix => package.Barcode.StartsWith(prefix)))
            {
                // 发送申通自动揽收请求
                var stoRequest = new StoAutoReceiveRequest
                {
                    WhCode = stoApiSettings.WhCode, // 从配置获取
                    OrgCode = stoApiSettings.OrgCode, // 从配置获取
                    UserCode = stoApiSettings.UserCode, // 从配置获取
                    Packages =
                    [
                        new Package()
                        {
                            WaybillNo = package.Barcode,
                            Weight = package.Weight.ToString("F2"),
                            OpTime = package.CreateTime.ToString("yyyy-MM-dd HH:mm:ss")
                        }
                    ]
                };

                var stoResponse = await _stoAutoReceiveService.SendAutoReceiveRequestAsync(stoRequest);
                if (stoResponse is { Success: true })
                {
                    Log.Information("申通自动揽收请求成功: {Barcode}", package.Barcode);
                }
                else
                {
                    Log.Error("申通自动揽收请求失败: {Barcode}, 错误: {ErrorMessage}", package.Barcode, stoResponse?.ErrorMsg);
                }
            }
            else
            {
                Log.Information("包裹 {Barcode} 条码不符合申通自动揽收前缀，跳过申通自动揽收。");
            }

            // 获取韵达API配置
            var yundaApiSettings = _settingsService.LoadSettings<Models.Yunda.Settings.YundaApiSettings>();
            var yundaPrefixes = yundaApiSettings.BarcodePrefixes.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();

            // 根据配置的条码前缀上传韵达重量
            if (yundaPrefixes.Any(prefix => package.Barcode.StartsWith(prefix)))
            {
                var yundaRequest = new YundaUploadWeightRequest
                {
                    PartnerId = yundaApiSettings.PartnerId,
                    Password = yundaApiSettings.Password,
                    Rc4Key = yundaApiSettings.Rc4Key,
                    Orders = new YundaOrders
                    {
                        GunId = yundaApiSettings.GunId,
                        RequestTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        OrderList =
                        [
                            new YundaOrder
                            {
                                // 随机生成一个19位数字作为唯一标志
                                Id = Random.Shared.NextInt64(1_000_000_000_000_000_000L, long.MaxValue),
                                DocId = long.Parse(package.Barcode), // 将DocId设置为Barcode
                                ScanSite = yundaApiSettings.ScanSite,
                                ScanTime = package.CreateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                                ScanMan = yundaApiSettings.ScanMan,
                                ObjWei = (decimal)package.Weight
                            }
                        ]
                    }
                };

                var yundaResponse = await _yundaUploadWeightService.SendUploadWeightRequestAsync(yundaRequest);
                if (yundaResponse is { Result: true, Code: "0000" })
                {
                    Log.Information("韵达上传重量请求成功: {Barcode}", package.Barcode);
                }
                else
                {
                    Log.Error("韵达上传重量请求失败: {Barcode}, 错误: {Message}, {ErrorCode}-{ErrorMsg}",
                        package.Barcode, yundaResponse?.Message, yundaResponse?.Data?.ErrorCode, yundaResponse?.Data?.ErrorMsg);
                }
            }
            else
            {
                Log.Information("包裹 {Barcode} 条码不符合韵达上传重量前缀，跳过韵达上传重量。");
            }

            // 获取中通API配置
            var ztoApiSettings = _settingsService.LoadSettings<ShanghaiModuleBelt.Models.Zto.Settings.ZtoApiSettings>();
            var ztoPrefixes = ztoApiSettings.BarcodePrefixes.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();

            // 根据配置的条码前缀上传中通揽收
            if (ztoPrefixes.Any(package.Barcode.StartsWith))
            {
                var ztoRequest = new Models.Zto.CollectUploadRequest
                {
                    CollectUploadDTOS =
                    [
                        new Models.Zto.CollectUploadDTO
                        {
                            BillCode = package.Barcode,
                            Weight = (decimal)package.Weight,
                        }
                    ]
                };

                var ztoResponse = await _ztoApiService.UploadCollectTraceAsync(ztoRequest);

                if (ztoResponse is { Status: true })
                {
                    Log.Information("中通揽收上传请求成功: {Barcode}", package.Barcode);
                }
                else
                {
                    Log.Error("中通揽收上传请求失败: {Barcode}, 错误: {Message}, Code={Code}",
                        package.Barcode, ztoResponse.Message, ztoResponse.Code);
                }
            }
            else
            {
                Log.Information("包裹 {Barcode} 条码不符合中通揽收前缀，跳过中通揽收。");
            }

            // 获取极兔API配置
            var jituApiSettings = _settingsService.LoadSettings<Modules.Models.Jitu.Settings.JituApiSettings>();
            var jituPrefixes = jituApiSettings.BarcodePrefixes.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();

            // 根据配置的条码前缀上传极兔OpScan
            if (jituPrefixes.Any(prefix => package.Barcode.StartsWith(prefix)))
            {
                var jituRequest = new Modules.Models.Jitu.JituOpScanRequest
                {
                    Billcode = package.Barcode,
                    Weight = package.Weight,
                    Length = package.Length ?? 0,
                    Width = package.Width ?? 0,
                    Height = package.Height ?? 0,
                    Devicecode = jituApiSettings.DeviceCode,
                    Devicename = jituApiSettings.DeviceName,
                    Imgpath = package.ImagePath ?? string.Empty
                };

                var jituResponse = await _jituService.SendOpScanRequestAsync(jituRequest);

                if (jituResponse is { Success: true, Code: 200 })
                {
                    Log.Information("极兔OpScan上传请求成功: {Barcode}", package.Barcode);
                }
                else
                {
                    Log.Error("极兔OpScan上传请求失败: {Barcode}, 错误: {Message}, Code={Code}",
                        package.Barcode, jituResponse.Message, jituResponse.Code);
                }
            }
            else
            {
                Log.Information("包裹 {Barcode} 条码不符合极兔OpScan上传前缀，跳过极兔OpScan上传。");
            }

            // 更新UI
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    // 更新当前条码和图像
                    CurrentBarcode = package.Barcode;
                    // 更新实时包裹数据
                    UpdatePackageInfoItems(package);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "更新UI时发生错误");
                }
            });
            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    // 更新历史记录
                    PackageHistory.Insert(0, package);
                    while (PackageHistory.Count > 1000) // 保持最近1000条记录
                        PackageHistory.RemoveAt(PackageHistory.Count - 1);
                    // 更新统计数据
                    UpdateStatistics();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "更新统计信息时发生错误");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹信息时发生错误：{Barcode}", package.Barcode);
            package.SetStatus(PackageStatus.Error,$"处理失败：{ex.Message}");
        }
    }

    private void UpdatePackageInfoItems(PackageInfo package)
    {
        var weightItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "重量");
        if (weightItem != null)
        {
            weightItem.Value = package.Weight.ToString("F2");
            weightItem.Unit = "kg";
        }

        var sizeItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "尺寸");
        if (sizeItem != null)
        {
            sizeItem.Value = package.VolumeDisplay;
            sizeItem.Unit = "mm";
        }

        var timeItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "时间");
        if (timeItem != null)
        {
            timeItem.Value = package.CreateTime.ToString("HH:mm:ss");
            timeItem.Description = $"处理于 {package.CreateTime:yyyy-MM-dd}";
        }

        var statusItem = PackageInfoItems.FirstOrDefault(static x => x.Label == "状态");
        if (statusItem == null) return;

        statusItem.Value = package.StatusDisplay;
        statusItem.Description = package.ErrorMessage ?? "处理状态";
    }

    private void UpdateStatistics()
    {
        var totalItem = StatisticsItems.FirstOrDefault(static x => x.Label == "总包裹数");
        if (totalItem != null)
        {
            totalItem.Value = PackageHistory.Count.ToString();
            totalItem.Description = $"累计处理 {PackageHistory.Count} 个包裹";
        }

        var successItem = StatisticsItems.FirstOrDefault(static x => x.Label == "成功数");
        if (successItem != null)
        {
            var successCount = PackageHistory.Count(static p => string.IsNullOrEmpty(p.ErrorMessage));
            successItem.Value = successCount.ToString();
            successItem.Description = $"成功处理 {successCount} 个包裹";
        }

        var failedItem = StatisticsItems.FirstOrDefault(static x => x.Label == "失败数");
        if (failedItem != null)
        {
            var failedCount = PackageHistory.Count(static p => !string.IsNullOrEmpty(p.ErrorMessage));
            failedItem.Value = failedCount.ToString();
            failedItem.Description = $"失败处理 {failedCount} 个包裹";
        }

        var rateItem = StatisticsItems.FirstOrDefault(static x => x.Label == "处理速率");
        if (rateItem == null) return;

        {
            var hourAgo = DateTime.Now.AddHours(-1);
            var hourlyCount = PackageHistory.Count(p => p.CreateTime > hourAgo);
            rateItem.Value = hourlyCount.ToString();
            rateItem.Description = $"最近一小时处理 {hourlyCount} 个";
        }
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
            try
            {
                // 停止定时器
                _timer.Stop();

                // 取消事件订阅
                _cameraService.ConnectionChanged -= OnCameraConnectionChanged;
                _moduleConnectionService.ConnectionStateChanged -= OnModuleConnectionChanged;

                // 释放订阅
                foreach (var subscription in _subscriptions) subscription.Dispose();
                _subscriptions.Clear();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "释放资源时发生错误");
            }

        _disposed = true;
    }
}
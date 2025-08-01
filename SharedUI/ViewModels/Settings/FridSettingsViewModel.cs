using System.Collections.ObjectModel;
using System.IO.Ports;
using Common.Services.Settings;
using Common.Services.Ui;
using Prism.Commands;
using Prism.Mvvm;
using Common.Models.Settings;
using SharedUI.Converters;
using Serilog;

namespace SharedUI.ViewModels.Settings;

/// <summary>
/// Frid设置视图模型
/// </summary>
public class FridSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private readonly INotificationService _notificationService;
    private FridSettings _configuration = new();

    public FridSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;

        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);
        RefreshPortsCommand = new DelegateCommand(ExecuteRefreshPorts);

        LoadSettings();
        RefreshPorts();
    }

    /// <summary>
    /// 配置对象
    /// </summary>
    public FridSettings Configuration
    {
        get => _configuration;
        set => SetProperty(ref _configuration, value);
    }

    /// <summary>
    /// 可用的串口列表
    /// </summary>
    public ObservableCollection<string> AvailablePorts { get; } = [];

    /// <summary>
    /// 可用的波特率列表
    /// </summary>
    public ObservableCollection<int> AvailableBaudRates { get; } = [9600, 19200, 38400, 57600, 115200];

    /// <summary>
    /// 可用的数据位列表
    /// </summary>
    public ObservableCollection<int> AvailableDataBits { get; } = [5, 6, 7, 8];

    /// <summary>
    /// 可用的停止位列表
    /// </summary>
    public ObservableCollection<int> AvailableStopBits { get; } = [1, 2];

    /// <summary>
    /// 可用的校验位列表
    /// </summary>
    public ObservableCollection<ParityOption> AvailableParity { get; } = [];

    /// <summary>
    /// 连接类型列表
    /// </summary>
    public static Array ConnectionTypes => Enum.GetValues(typeof(FridConnectionType));

    /// <summary>
    /// 保存配置命令
    /// </summary>
    public DelegateCommand SaveConfigurationCommand { get; }

    /// <summary>
    /// 刷新串口命令
    /// </summary>
    public DelegateCommand RefreshPortsCommand { get; }

    /// <summary>
    /// 执行保存配置
    /// </summary>
    private void ExecuteSaveConfiguration()
    {
        try
        {
            _settingsService.SaveSettings(Configuration);
            _notificationService.ShowSuccess("Frid设置保存成功");
            Log.Information("Frid设置已保存");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"保存Frid设置失败: {ex.Message}");
            Log.Error(ex, "保存Frid设置失败");
        }
    }

    /// <summary>
    /// 执行刷新串口
    /// </summary>
    private void ExecuteRefreshPorts()
    {
        try
        {
            RefreshPorts();
            _notificationService.ShowSuccess("串口列表已刷新");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"刷新串口列表失败: {ex.Message}");
            Log.Error(ex, "刷新串口列表失败");
        }
    }

    /// <summary>
    /// 加载设置
    /// </summary>
    private void LoadSettings()
    {
        try
        {
            Configuration = _settingsService.LoadSettings<FridSettings>();
            InitializeParityOptions();
            Log.Information("Frid设置加载成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载Frid设置失败，使用默认配置");
            Configuration = new FridSettings();
            InitializeParityOptions();
        }
    }

    /// <summary>
    /// 刷新串口列表
    /// </summary>
    private void RefreshPorts()
    {
        AvailablePorts.Clear();
        var ports = SerialPort.GetPortNames();
        foreach (var port in ports)
        {
            AvailablePorts.Add(port);
        }
    }

    /// <summary>
    /// 初始化校验位选项
    /// </summary>
    private void InitializeParityOptions()
    {
        AvailableParity.Clear();
        AvailableParity.Add(new ParityOption { Value = 0, Description = "无" });
        AvailableParity.Add(new ParityOption { Value = 1, Description = "奇校验" });
        AvailableParity.Add(new ParityOption { Value = 2, Description = "偶校验" });
    }
}

/// <summary>
/// 校验位选项
/// </summary>
public class ParityOption
{
    public int Value { get; set; }
    public string Description { get; set; } = string.Empty;
} 
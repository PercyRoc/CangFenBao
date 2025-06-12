using Common.Services.Settings;
using XinBeiYang.ViewModels;

namespace XinBeiYang.Models;

[Configuration("HostConfiguration")]
public class HostConfiguration : BindableBase
{
    // 设备基本配置
    private string _deviceId = "DEV001"; // 默认设备编号 (也用作扫描仪序号和主设备号 - 根据用户要求)
    private readonly int _vendorId = 5; // 厂商序号: 5 = 新北洋 (固定)
    private readonly int _deviceType = 4; // 设备类型: 4 = 交叉带分拣机（直线）(固定)
    
    // PLC 配置
    private string _plcIpAddress = "127.0.0.1";
    private int _plcPort = 8080;
    private int _uploadAckTimeoutSeconds = 10; // 默认10秒等待初始确认超时
    private int _uploadResultTimeoutSeconds = 60; // 默认60秒等待最终结果超时
    private int _uploadCountdownSeconds = 5; // 默认5秒倒计时
    
    // *** 向后兼容：保留原有的 UploadTimeoutSeconds 属性 ***
    private int _uploadTimeoutSeconds = 60; // 默认60秒超时（向后兼容）

    // 京东服务配置
    private string _jdIpAddress = "127.0.0.1";
    private int _jdPort = 8088;
    private string _jdLocalHttpUrlPrefix = "http://localhost:8080/images/"; // 默认本地图片URL前缀
    
    // 条码模式配置
    private BarcodeMode _barcodeMode = BarcodeMode.MultiBarcode; // 默认多条码模式

    // 设备基本配置属性
    public string DeviceId
    {
        get => _deviceId;
        set => SetProperty(ref _deviceId, value);
    }
    
    // PLC 配置属性
    public string PlcIpAddress
    {
        get => _plcIpAddress;
        set => SetProperty(ref _plcIpAddress, value);
    }

    public int PlcPort
    {
        get => _plcPort;
        set => SetProperty(ref _plcPort, value);
    }

    /// <summary>
    /// 等待PLC初始确认的超时时间（秒）
    /// </summary>
    public int UploadAckTimeoutSeconds
    {
        get => _uploadAckTimeoutSeconds;
        set => SetProperty(ref _uploadAckTimeoutSeconds, value);
    }
    
    /// <summary>
    /// 等待PLC最终结果的超时时间（秒）
    /// </summary>
    public int UploadResultTimeoutSeconds
    {
        get => _uploadResultTimeoutSeconds;
        set => SetProperty(ref _uploadResultTimeoutSeconds, value);
    }
    
    /// <summary>
    /// 向后兼容：上包超时时间（秒）- 现在映射到最终结果超时时间
    /// </summary>
    public int UploadTimeoutSeconds
    {
        get => _uploadTimeoutSeconds;
        set => SetProperty(ref _uploadTimeoutSeconds, value);
    }
    
    public int UploadCountdownSeconds
    {
        get => _uploadCountdownSeconds;
        set => SetProperty(ref _uploadCountdownSeconds, value);
    }
    
    // 京东服务配置属性
    public string JdIpAddress
    {
        get => _jdIpAddress;
        set => SetProperty(ref _jdIpAddress, value);
    }

    public int JdPort
    {
        get => _jdPort;
        set => SetProperty(ref _jdPort, value);
    }

    public string JdLocalHttpUrlPrefix
    {
        get => _jdLocalHttpUrlPrefix;
        set => SetProperty(ref _jdLocalHttpUrlPrefix, value);
    }
    
    // 条码模式配置属性
    public BarcodeMode BarcodeMode
    {
        get => _barcodeMode;
        set => SetProperty(ref _barcodeMode, value);
    }

    // 京东协议相关固定配置
    public int VendorId
    {
        get => _vendorId;
        // 一般不允许修改，如果需要，添加 set
        // set => SetProperty(ref _vendorId, value);
    }

    public int DeviceType
    {
        get => _deviceType;
        // 一般不允许修改，如果需要，添加 set
        // set => SetProperty(ref _deviceType, value);
    }

    // 向后兼容的属性（重定向到 PlcIpAddress 和 PlcPort）
    public string IpAddress
    {
        get => PlcIpAddress;
        set => PlcIpAddress = value;
    }

    public int Port
    {
        get => PlcPort;
        set => PlcPort = value;
    }
}
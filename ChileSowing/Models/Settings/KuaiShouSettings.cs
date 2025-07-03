using Common.Services.Settings;
using Prism.Mvvm;

namespace ChileSowing.Models.Settings;

/// <summary>
/// 快手接口配置
/// </summary>
[Configuration("KuaiShouSettings")]
public class KuaiShouSettings : BindableBase
{
    private bool _isEnabled = true;
    private string _apiUrl = "http://10.20.160.36:7001/ydadl/public/common/new/commitScanMsg.do";
    private string _user = "1002";
    private string _password = "202cb962ac59075b964b07152d234b70";
    private string _deviceNum = "220579605500023";
    private string _scanPerson = "1002";
    private int _scanType = 18;
    private int _inductionId = 1;
    private int _prodLine = 1;
    private int _equipmentId = 10000046;
    private int _placeCode = 200000;
    private string _areaCode = "52";
    private string _expProdType = "YD";
    private int _objId = 20;
    private float _defaultWeight = 0.2f;
    private int _timeoutMs = 5000;
    private bool _enableDetailedLogging = true;
    private int _retryCount = 3;
    private int _retryDelayMs = 1000;
    
    // 新增字段 - 包裹尺寸相关
    private string _remarkField = "n";
    private int _length = 10;
    private int _width = 6;
    private int _height = 3;
    private int _volume = 180;

    /// <summary>
    /// 是否启用快手接口
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    /// <summary>
    /// 快手接口地址 (UAT环境)
    /// </summary>
    public string ApiUrl
    {
        get => _apiUrl;
        set => SetProperty(ref _apiUrl, value);
    }

    /// <summary>
    /// 快手设备号 - 扫描设备的唯一标识
    /// </summary>
    public string DeviceNum
    {
        get => _deviceNum;
        set => SetProperty(ref _deviceNum, value);
    }

    /// <summary>
    /// 扫描人员 - 操作人员ID或姓名
    /// </summary>
    public string ScanPerson
    {
        get => _scanPerson;
        set => SetProperty(ref _scanPerson, value);
    }

    /// <summary>
    /// 扫描类型
    /// </summary>
    public int ScanType
    {
        get => _scanType;
        set => SetProperty(ref _scanType, value);
    }

    /// <summary>
    /// 供件台 - 包裹进入分拣机的入口ID
    /// </summary>
    public int InductionId
    {
        get => _inductionId;
        set => SetProperty(ref _inductionId, value);
    }

    /// <summary>
    /// 生产线编码
    /// </summary>
    public int ProdLine
    {
        get => _prodLine;
        set => SetProperty(ref _prodLine, value);
    }

    /// <summary>
    /// 交叉带设备ID
    /// </summary>
    public int EquipmentId
    {
        get => _equipmentId;
        set => SetProperty(ref _equipmentId, value);
    }

    /// <summary>
    /// 场地编码
    /// </summary>
    public int PlaceCode
    {
        get => _placeCode;
        set => SetProperty(ref _placeCode, value);
    }

    /// <summary>
    /// 片区编码
    /// </summary>
    public string AreaCode
    {
        get => _areaCode;
        set => SetProperty(ref _areaCode, value);
    }

    /// <summary>
    /// 产品类型
    /// </summary>
    public string ExpProdType
    {
        get => _expProdType;
        set => SetProperty(ref _expProdType, value);
    }

    /// <summary>
    /// 货样编码
    /// </summary>
    public int ObjId
    {
        get => _objId;
        set => SetProperty(ref _objId, value);
    }

    /// <summary>
    /// 默认重量 (当无法获取实际重量时使用)
    /// </summary>
    public float DefaultWeight
    {
        get => _defaultWeight;
        set => SetProperty(ref _defaultWeight, value);
    }

    /// <summary>
    /// 请求超时时间 (毫秒)
    /// </summary>
    public int TimeoutMs
    {
        get => _timeoutMs;
        set => SetProperty(ref _timeoutMs, value);
    }

    /// <summary>
    /// 是否记录详细日志
    /// </summary>
    public bool EnableDetailedLogging
    {
        get => _enableDetailedLogging;
        set => SetProperty(ref _enableDetailedLogging, value);
    }

    /// <summary>
    /// 重试次数
    /// </summary>
    public int RetryCount
    {
        get => _retryCount;
        set => SetProperty(ref _retryCount, value);
    }

    /// <summary>
    /// 重试间隔 (毫秒)
    /// </summary>
    public int RetryDelayMs
    {
        get => _retryDelayMs;
        set => SetProperty(ref _retryDelayMs, value);
    }

    /// <summary>
    /// 快手用户名 - 接口认证用户名
    /// </summary>
    public string User
    {
        get => _user;
        set => SetProperty(ref _user, value);
    }

    /// <summary>
    /// 快手密码 - 接口认证密码（当前为MD5值：202cb962ac59075b964b07152d234b70，对应明文密码'hello'）
    /// </summary>
    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    /// <summary>
    /// 备注字段 - 用于特殊标记或备注信息
    /// </summary>
    public string RemarkField
    {
        get => _remarkField;
        set => SetProperty(ref _remarkField, value);
    }

    /// <summary>
    /// 包裹长度 (厘米)
    /// </summary>
    public int Length
    {
        get => _length;
        set => SetProperty(ref _length, value);
    }

    /// <summary>
    /// 包裹宽度 (厘米)
    /// </summary>
    public int Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    /// <summary>
    /// 包裹高度 (厘米)
    /// </summary>
    public int Height
    {
        get => _height;
        set => SetProperty(ref _height, value);
    }

    /// <summary>
    /// 包裹体积 (立方厘米)
    /// </summary>
    public int Volume
    {
        get => _volume;
        set => SetProperty(ref _volume, value);
    }
} 
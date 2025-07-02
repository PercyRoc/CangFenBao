using Common.Services.Settings;

namespace ZtCloudWarehous.ViewModels.Settings;

[Configuration("weighing")]
internal class WeighingSettings : BindableBase
{
    private const string UatBaseUrl = "https://scm-gateway-uat.ztocwst.com/edi/service/inbound/bz";
    private const string ProdBaseUrl = "https://scm-openapi.ztocwst.com/edi/service/inbound/bz";
    // 公共参数
    private string _api = "shanhaitong.wms.dws.weight";

    private string _appKey = "d8318f010e97988a0bcfd8910a133812";

    private string _companyCode = "MXKJ";
    private decimal _defaultWeight;

    private string _equipmentCode = string.Empty;
    private bool _isProduction;
    private string _newWeighingEnvironment = "uat"; // uat, ver, prod

    private string _packagingMaterialCode = string.Empty;

    private string _secret = "20a0c2e787cec2e887069aa79927f2c5";

    private string _sign = string.Empty;

    private string _tenantId = string.Empty;

    // 新称重接口配置
    private bool _useNewWeighingApi;
    private string _userId = string.Empty;

    private string _userRealName = string.Empty;

    private string _warehouseCode = string.Empty;

    public bool IsProduction
    {
        get => _isProduction;
        set => SetProperty(ref _isProduction, value);
    }

    public string Api
    {
        get => _api;
        set => SetProperty(ref _api, value);
    }

    public string CompanyCode
    {
        get => _companyCode;
        set => SetProperty(ref _companyCode, value);
    }

    public string AppKey
    {
        get => _appKey;
        set => SetProperty(ref _appKey, value);
    }

    public string Secret
    {
        get => _secret;
        set => SetProperty(ref _secret, value);
    }

    public string Sign
    {
        get => _sign;
        set => SetProperty(ref _sign, value);
    }

    public string TenantId
    {
        get => _tenantId;
        set => SetProperty(ref _tenantId, value);
    }

    public string WarehouseCode
    {
        get => _warehouseCode;
        set => SetProperty(ref _warehouseCode, value);
    }

    public string EquipmentCode
    {
        get => _equipmentCode;
        set => SetProperty(ref _equipmentCode, value);
    }

    public string UserRealName
    {
        get => _userRealName;
        set => SetProperty(ref _userRealName, value);
    }

    public string PackagingMaterialCode
    {
        get => _packagingMaterialCode;
        set => SetProperty(ref _packagingMaterialCode, value);
    }

    public string UserId
    {
        get => _userId;
        set => SetProperty(ref _userId, value);
    }

    public decimal DefaultWeight
    {
        get => _defaultWeight;
        set => SetProperty(ref _defaultWeight, value);
    }

    /// <summary>
    ///     是否使用新称重接口
    /// </summary>
    public bool UseNewWeighingApi
    {
        get => _useNewWeighingApi;
        set => SetProperty(ref _useNewWeighingApi, value);
    }

    /// <summary>
    ///     新称重接口环境 (uat, ver, prod)
    /// </summary>
    public string NewWeighingEnvironment
    {
        get => _newWeighingEnvironment;
        set => SetProperty(ref _newWeighingEnvironment, value);
    }

    public string ApiUrl
    {
        get => IsProduction ? ProdBaseUrl : UatBaseUrl;
    }

    /// <summary>
    ///     新称重接口URL
    /// </summary>
    public string NewWeighingApiUrl
    {
        get => NewWeighingEnvironment.ToLower() switch
        {
            "uat" => "https://anapi-uat.annto.com/bop/T201904230000000014/xjyc/weighing",
            "ver" => "https://anapi-ver.annto.com/bop/T201904230000000014/xjyc/weighing",
            "prod" => "https://anapi.annto.com/bop/T201904230000000014/xjyc/weighing",
            _ => "https://anapi-uat.annto.com/bop/T201904230000000014/xjyc/weighing"
        };
    }
}
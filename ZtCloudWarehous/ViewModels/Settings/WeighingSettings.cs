using Common.Services.Settings;
using Prism.Mvvm;

namespace ZtCloudWarehous.ViewModels.Settings;

[Configuration("weighing")]
internal class WeighingSettings : BindableBase
{
    // 公共参数
    private string _api = "shanhaitong.wms.dws.weight";

    private string _appKey = "d8318f010e97988a0bcfd8910a133812";

    private string _companyCode = "MXKJ";

    private string _equipmentCode = string.Empty;
    private bool _isProduction;

    private string _secret = "20a0c2e787cec2e887069aa79927f2c5";

    private string _sign = string.Empty;

    private string _tenantId = string.Empty;

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
}
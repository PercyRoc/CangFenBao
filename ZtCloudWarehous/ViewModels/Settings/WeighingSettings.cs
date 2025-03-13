using Common.Services.Settings;
using Prism.Mvvm;

namespace Presentation_ZtCloudWarehous.ViewModels.Settings;

[Configuration("weighing")]
public class WeighingSettings : BindableBase
{
    private bool _isProduction;
    public bool IsProduction
    {
        get => _isProduction;
        set => SetProperty(ref _isProduction, value);
    }

    // 公共参数
    private string _api = "shanhaitong.wms.dws.weight";
    public string Api
    {
        get => _api;
        set => SetProperty(ref _api, value);
    }

    private string _companyCode = "MXKJ";
    public string CompanyCode
    {
        get => _companyCode;
        set => SetProperty(ref _companyCode, value);
    }

    private string _appKey = "d8318f010e97988a0bcfd8910a133812";
    public string AppKey
    {
        get => _appKey;
        set => SetProperty(ref _appKey, value);
    }

    private string _secret = "20a0c2e787cec2e887069aa79927f2c5";
    public string Secret
    {
        get => _secret;
        set => SetProperty(ref _secret, value);
    }

    private string _sign = string.Empty;
    public string Sign
    {
        get => _sign;
        set => SetProperty(ref _sign, value);
    }

    private string _tenantId = string.Empty;
    public string TenantId
    {
        get => _tenantId;
        set => SetProperty(ref _tenantId, value);
    }

    private string _warehouseCode = string.Empty;
    public string WarehouseCode
    {
        get => _warehouseCode;
        set => SetProperty(ref _warehouseCode, value);
    }

    private string _equipmentCode = string.Empty;
    public string EquipmentCode
    {
        get => _equipmentCode;
        set => SetProperty(ref _equipmentCode, value);
    }

    private string _userRealName = string.Empty;
    public string UserRealName
    {
        get => _userRealName;
        set => SetProperty(ref _userRealName, value);
    }
} 
using Common.Services.Settings;
using Prism.Mvvm;

namespace ZtCloudWarehous.ViewModels.Settings;

[Configuration("XiyiguApiSettings")]
public class XiyiguApiSettings: BindableBase
{
    private string _aesKey = "H1ToUe8qCdz2sfIZ";
    private string _baseUrl = "http://dx.y-open.com/dxiotmobile";
    private string _machineMx = "864797040291235";

    /// <summary>
    ///     基础URL
    /// </summary>
    public string BaseUrl
    {
        get => _baseUrl;
        set => SetProperty(ref _baseUrl, value);
    }

    /// <summary>
    ///     AES密钥
    /// </summary>
    public string AesKey
    {
        get => _aesKey;
        set => SetProperty(ref _aesKey, value);
    }

    /// <summary>
    ///     设备编号
    /// </summary>
    public string MachineMx
    {
        get => _machineMx;
        set => SetProperty(ref _machineMx, value);
    }
}
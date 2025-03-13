using Common.Models.Settings;
using Common.Services.Settings;
using Prism.Mvvm;

namespace Presentation_XiYiGu.Models.Settings;

/// <summary>
/// API设置
/// </summary>
[Configuration("ApiSettings")]
public class ApiSettings : BindableBase
{
    private bool _enabled = true;
    private string _baseUrl = "http://dx.y-open.com/dxiotmobile";
    private string _aesKey = "H1ToUe8qCdz2sfIZ";
    private string _machineMx = "864797040291235";
    private bool _autoUpload = true;

    /// <summary>
    /// 是否启用API
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    /// <summary>
    /// 基础URL
    /// </summary>
    public string BaseUrl
    {
        get => _baseUrl;
        set => SetProperty(ref _baseUrl, value);
    }

    /// <summary>
    /// AES密钥
    /// </summary>
    public string AesKey
    {
        get => _aesKey;
        set => SetProperty(ref _aesKey, value);
    }

    /// <summary>
    /// 设备编号
    /// </summary>
    public string MachineMx
    {
        get => _machineMx;
        set => SetProperty(ref _machineMx, value);
    }

    /// <summary>
    /// 是否自动上传
    /// </summary>
    public bool AutoUpload
    {
        get => _autoUpload;
        set => SetProperty(ref _autoUpload, value);
    }
} 
using Common.Services.Settings;
using Newtonsoft.Json;

namespace ShanghaiModuleBelt.Models.Yunda.Settings;

/// <summary>
///     韵达API配置
/// </summary>
[Configuration("YundaApiSettings")]
public class YundaApiSettings
{
    /// <summary>
    ///     韵达API网关地址
    ///     测试环境: https://u-openapi.yundasys.com/openapi/outer/upLoadWeight
    ///     生产环境: https://openapi.yundaex.com/openapi/outer/upLoadWeight
    /// </summary>
    public string ApiUrl { get; set; } = "https://u-openapi.yundasys.com/openapi/outer/upLoadWeight";

    /// <summary>
    ///     开放平台发放的app-key
    /// </summary>
    public string AppKey { get; set; } = "004060";

    /// <summary>
    ///     开放平台发放的app-secret
    /// </summary>
    public string AppSecret { get; set; } = "50c0a7cfdfaa4bcc8a9c7a67c560316e";

    /// <summary>
    ///     合作商 id
    /// </summary>
    public string PartnerId { get; set; } = "225317100063";

    /// <summary>
    ///     合作商密码
    /// </summary>
    public string Password { get; set; } = "H74JEgYCnhw2ZGmIX3UK5MNBpskVdz";

    /// <summary>
    ///     密钥
    /// </summary>
    public string Rc4Key { get; set; } = "tjpxX2iAdHvZrcys";

    /// <summary>
    ///     称重机器序列号
    /// </summary>
    public long GunId { get; set; } = 82102042523419L;

    /// <summary>
    ///     扫描站点
    /// </summary>
    public int ScanSite { get; set; } = 225317;

    /// <summary>
    ///     扫描员编码
    /// </summary>
    [JsonProperty(nameof(ScanMan))]
    public string ScanMan { get; set; } = "1001";

    /// <summary>
    ///     韵达上传重量的条码前缀，多个用分号分隔
    /// </summary>
    [JsonProperty(nameof(BarcodePrefixes))]
    public string BarcodePrefixes { get; set; } = "43;46;32"; // 默认值
}
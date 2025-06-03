using Common.Services.Settings;

namespace ShanghaiModuleBelt.Models.Yunda.Settings;

/// <summary>
/// 韵达API配置
/// </summary>
[Configuration("YundaApiSettings")]
public class YundaApiSettings
{
    /// <summary>
    /// 韵达API网关地址
    /// 测试环境: https://u-openapi.yundasys.com/openapi/outer/upLoadWeight
    /// 生产环境: https://openapi.yundaex.com/openapi/outer/upLoadWeight
    /// </summary>
    public string ApiUrl { get; set; } = "https://u-openapi.yundasys.com/openapi/outer/upLoadWeight";

    /// <summary>
    /// 开放平台发放的app-key
    /// </summary>
    public string AppKey { get; set; } = "999999";

    /// <summary>
    /// 开放平台发放的app-secret
    /// </summary>
    public string AppSecret { get; set; } = "04d4ad40eeec11e9bad2d962f53dda9d";

    /// <summary>
    /// 合作商 id
    /// </summary>
    public string PartnerId { get; set; } = "1233241018";

    /// <summary>
    /// 合作商密码
    /// </summary>
    public string Password { get; set; } = "123456";

    /// <summary>
    /// 密钥
    /// </summary>
    public string Rc4Key { get; set; } = "78mDqxQZt62BcVrR";

    /// <summary>
    /// 称重机器序列号
    /// </summary>
    public long GunId { get; set; } = 141000170110012;

    /// <summary>
    /// 扫描站点
    /// </summary>
    public int ScanSite { get; set; } = 436101;

    /// <summary>
    /// 扫描员编码
    /// </summary>
    public string ScanMan { get; set; } = "1222";
} 
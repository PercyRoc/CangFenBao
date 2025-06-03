using Common.Services.Settings;

namespace ShanghaiModuleBelt.Models.Sto.Settings;

/// <summary>
/// 申通快递API配置
/// </summary>
[Configuration("StoApiSettings")]
public class StoApiSettings
{
    /// <summary>
    /// 申通API网关地址
    /// </summary>
    public string ApiUrl { get; set; } = "http://cloudinter-linkgatewaytest.sto.cn/gateway/link.do";

    /// <summary>
    /// 订阅方/请求发起方的应用key
    /// </summary>
    public string FromAppkey { get; set; } = "CAKKtKkjwqmkCXn";

    /// <summary>
    /// 订阅方/请求发起方的应用资源code
    /// </summary>
    public string FromCode { get; set; } = "CAKKtKkjwqmkCXn";

    /// <summary>
    /// 申通API密钥，用于签名
    /// </summary>
    public string AppSecret { get; set; } = "c9tscvY7oreshG1KsIYfVJLIdUT1v4Xg";

    /// <summary>
    /// 接口名称
    /// </summary>
    public string ApiName { get; set; } = "GALAXY_CANGKU_AUTO_NEW";

    /// <summary>
    /// 接收方应用key
    /// </summary>
    public string ToAppkey { get; set; } = "galaxy_receive";

    /// <summary>
    /// 接收方应用资源code
    /// </summary>
    public string ToCode { get; set; } = "galaxy_receive";

    /// <summary>
    /// 仓编码
    /// </summary>
    public string WhCode { get; set; } = "your_wh_code";

    /// <summary>
    /// 揽收网点编码
    /// </summary>
    public string OrgCode { get; set; } = "your_org_code";

    /// <summary>
    /// 揽收员编码
    /// </summary>
    public string UserCode { get; set; } = "your_user_code";
} 
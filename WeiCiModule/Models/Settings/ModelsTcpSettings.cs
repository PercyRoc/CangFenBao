using Common.Services.Settings;

namespace WeiCiModule.Models.Settings;

/// <summary>
/// 模组带TCP连接设置
/// </summary>
[Configuration("ModelsTcpSettings")]
public class ModelsTcpSettings
{
    /// <summary>
    /// TCP服务器监听地址
    /// </summary>
    public string Address { get; set; } = "192.168.1.100";

    /// <summary>
    /// TCP服务器监听端口
    /// </summary>
    public int Port { get; set; } = 8080;

    /// <summary>
    /// 最小等待时间（毫秒）
    /// </summary>
    public int MinWaitTime { get; set; } = 100;

    /// <summary>
    /// 最大等待时间（毫秒）
    /// </summary>
    public int MaxWaitTime { get; set; } = 2000;

    /// <summary>
    ///     异常格口号（例如，超时）
    /// </summary>
    public int ExceptionChute { get; set; } = 999;

    /// <summary>
    ///     不在规则内条码的格口号
    /// </summary>
    public int NoRuleChute { get; set; } = 998;
} 
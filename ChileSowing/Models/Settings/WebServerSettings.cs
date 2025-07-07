using Common.Services.Settings;

namespace ChileSowing.Models.Settings;

/// <summary>
/// Web服务器设置
/// </summary>
[Configuration("WebServerSettings")]
public class WebServerSettings
{
    /// <summary>
    /// 是否启用Web服务器
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 服务器端口
    /// </summary>
    public int Port { get; set; } = 8080;

    /// <summary>
    /// 服务器主机地址
    /// </summary>
    public string Host { get; set; } = "*";

    /// <summary>
    /// 应用程序名称（用于构建URL路径）
    /// </summary>
    public string AppName { get; set; } = "chile-sowing";

    /// <summary>
    /// 完整的服务器URL
    /// </summary>
    public string ServerUrl => $"http://{(Host == "*" ? "localhost" : Host)}:{Port}";

    /// <summary>
    /// API基础路径
    /// </summary>
    public string ApiBasePath => $"/{AppName}";

    /// <summary>
    /// 分拣单数据同步接口的完整URL
    /// </summary>
    public string BatchOrderSyncUrl => $"{ServerUrl}{ApiBasePath}/send_batch_order_info";
} 
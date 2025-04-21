using Common.Services.Settings;

namespace ShanghaiFaxunLogistics.Models.ASN
{
    /// <summary>
    /// ASN服务配置
    /// </summary>
    [Configuration("AsnSettings")]
    public class AsnSettings
    {
        /// <summary>
        /// 系统编码
        /// </summary>
        public string SystemCode { get; set; } = "SH_FX";

        /// <summary>
        /// 仓库编码
        /// </summary>
        public string HouseCode { get; set; } = "SH_FX";

        /// <summary>
        /// HTTP服务监听地址
        /// </summary>
        public string HttpServerUrl { get; set; } = "http://0.0.0.0:9000";

        /// <summary>
        /// 应用名称
        /// </summary>
        public string ApplicationName { get; set; } = "api";

        /// <summary>
        /// 是否启用服务
        /// </summary>
        public bool IsEnabled { get; set; } = true;
    }
} 
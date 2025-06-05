using Common.Services.Settings;

namespace FuzhouPolicyForce.Models
{
    [Configuration("ShenTongLanShou")]
    public class ShenTongLanShouConfig : BindableBase
    {
        /// <summary>
        /// API地址
        /// </summary>
        public string? ApiUrl { get; set; } = "https://cloudinter-linkgateway.sto.cn/gateway/link.do";
        
        /// <summary>
        /// API名称
        /// </summary>
        public string? ApiName { get; set; } = "GALAXY_CANGKU_AUTO_NEW";
        
        /// <summary>
        /// 应用Key
        /// </summary>
        public string? FromAppKey { get; set; }
        
        /// <summary>
        /// 应用Code
        /// </summary>
        public string? FromCode { get; set; }
        
        /// <summary>
        /// 目标应用Key（固定值）
        /// </summary>
        public string? ToAppkey { get; set; } = "galaxy_receive";
        
        /// <summary>
        /// 目标应用Code（固定值）
        /// </summary>
        public string? ToCode { get; set; } = "galaxy_receive";
        
        /// <summary>
        /// 应用密钥
        /// </summary>
        public string? AppSecret { get; set; }
        
        /// <summary>
        /// 仓编码
        /// </summary>
        public string? WhCode { get; set; }
        
        /// <summary>
        /// 揽收网点编码
        /// </summary>
        public string? OrgCode { get; set; }
        
        /// <summary>
        /// 揽收员编码
        /// </summary>
        public string? UserCode { get; set; }
    }
} 
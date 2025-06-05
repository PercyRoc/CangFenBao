using Common.Services.Settings;

namespace FuzhouPolicyForce.Models
{
    [Configuration("ShenTongLanShou")]
    public class ShenTongLanShouConfig : BindableBase
    {
        public string? ApiUrl { get; set; }
        public string? ApiName { get; set; }
        public string? FromAppKey { get; set; }
        public string? FromCode { get; set; }
        public string? ToAppkey { get; set; }
        public string? ToCode { get; set; }
        public string? AppSecret { get; set; }
        public string? WhCode { get; set; }
        public string? OrgCode { get; set; }
        public string? UserCode { get; set; }
    }
} 
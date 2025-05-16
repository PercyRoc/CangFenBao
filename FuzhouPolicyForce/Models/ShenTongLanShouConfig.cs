using Common.Services.Settings;

namespace FuzhouPolicyForce.Models
{
    [Configuration("ShenTongLanShou")]
    public class ShenTongLanShouConfig
    {
        public string? ApiUrl { get; set; }
        public string? FromAppKey { get; set; }
        public string? FromCode { get; set; }
        public string? SecretKey { get; set; }
        public string? WhCode { get; set; }
        public string? OrgCode { get; set; }
        public string? UserCode { get; set; }
    }
} 
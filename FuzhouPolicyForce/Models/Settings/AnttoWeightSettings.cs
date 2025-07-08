using Common.Services.Settings;
using System.ComponentModel;

namespace FuzhouPolicyForce.Models.Settings
{
    public enum AnttoWeightEnvironment
    {
        [Description("测试环境")]
        UAT,
        [Description("验证环境")]
        VER,
        [Description("生产环境")]
        PROD
    }

    [Configuration("AnttoWeightSettings")]
    public class AnttoWeightSettings
    {
        public AnttoWeightEnvironment SelectedEnvironment { get; set; } = AnttoWeightEnvironment.UAT;
    }
}
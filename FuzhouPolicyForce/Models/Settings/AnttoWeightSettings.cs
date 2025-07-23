using Common.Services.Settings;
using System.ComponentModel;

namespace FuzhouPolicyForce.Models.Settings
{
    public enum AnttoWeightEnvironment
    {
        [Description("测试环境")]
        Uat,
        [Description("验证环境")]
        Ver,
        [Description("生产环境")]
        Prod
    }

    [Configuration("AnttoWeightSettings")]
    public class AnttoWeightSettings
    {
        public AnttoWeightEnvironment SelectedEnvironment { get; set; } = AnttoWeightEnvironment.Uat;
    }
}
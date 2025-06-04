using Common.Services.Settings;
using Newtonsoft.Json;

namespace ShanghaiModuleBelt.Models.Zto.Settings
{
    [Configuration("ZtoApiSettings")]
    public class ZtoApiSettings
    {
        /// <summary>
        /// 应用配置参数（x-appKey）
        /// </summary>
        [JsonProperty(nameof(AppKey))]
        public string AppKey { get; set; } = "379270ae4c4580bdeff97";

        /// <summary>
        /// 应用秘钥
        /// </summary>
        [JsonProperty(nameof(Secret))]
        public string? Secret { get; set; } = "dda031a771b0a451e113c9ccf081cd72";

        /// <summary>
        /// 测试环境接口地址
        /// </summary>
        [JsonProperty(nameof(TestApiUrl))]
        public string TestApiUrl { get; set; } = "https://japi-test.zto.com/zto.trace.collectUpload";

        /// <summary>
        /// 正式环境接口地址
        /// </summary>
        [JsonProperty(nameof(FormalApiUrl))]
        public string FormalApiUrl { get; set; } = "https://japi.zto.com/zto.trace.collectUpload";

        /// <summary>
        /// 是否使用测试环境
        /// </summary>
        [JsonProperty(nameof(UseTestEnvironment))]
        public bool UseTestEnvironment { get; set; } = true; // 默认使用测试环境

        /// <summary>
        /// 中通揽收的条码前缀，多个用分号分隔
        /// </summary>
        [JsonProperty(nameof(BarcodePrefixes))]
        public string BarcodePrefixes { get; set; } = ""; // 默认空字符串，需要用户配置
    }
} 
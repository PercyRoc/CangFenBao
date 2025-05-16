using Common.Services.Settings; // For ConfigurationAttribute
using System.ComponentModel.DataAnnotations; // For validation attributes if needed in the future

namespace SowingWall.Models.Settings
{
    [Configuration("WangDianTongSettings")] // Settings will be saved in Settings/WangDianTongSettings.json
    public class WangDianTongSettings
    {
        /// <summary>
        /// 卖家标识
        /// </summary>
        public string Sid { get; set; } = "sxdl";

        /// <summary>
        /// 接口公钥
        /// </summary>
        public string AppKey { get; set; } = "sxdl_wms_wdt";

        /// <summary>
        /// 接口私钥
        /// </summary>
        public string AppSecret { get; set; } = "c363f3b52ac925ea4e7b4ba6f47886fc";

        /// <summary>
        /// 请求地址
        /// </summary>
        public string RequestUrl { get; set; } = ""; // Add a default value if applicable
    }
} 
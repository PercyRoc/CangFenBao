using System.Collections.Generic;
using Common.Services.Settings; // 仍然需要这个来获取 ConfigurationAttribute

namespace WeiCiModule.Models
{
    [Configuration("BarcodeChuteMappings")] // 文件名仍为 BarcodeChuteMappings.json
    public class BarcodeChuteMappingSettings
    {
        public List<BarcodeChuteMapping> Mappings { get; set; } = [];
    }
}
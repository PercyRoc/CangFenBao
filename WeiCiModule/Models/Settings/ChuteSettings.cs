using Common.Services.Settings;


namespace WeiCiModule.Models.Settings
{
    [Configuration("ChuteSettings")]
    public class ChuteSettings
    {
        public List<ChuteSettingData> Items { get; set; } = [];
    }
} 
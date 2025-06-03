using Common.Services.Settings;

namespace JinHuaQiHang.Models.Settings
{
    [Configuration("YunDaUploadSettings")]
    public class YunDaUploadSettings
    {
        public string AppKey { get; set; } // 对应API文档中的app-key

        public string Secret { get; set; }

        public string PartnerId { get; set; }

        public string Password { get; set; }

        public string Rc4Key { get; set; }

        public long GunId { get; set; }

        public int ScanSite { get; set; }

        public string ScanMan { get; set; }

        public string UploadUrl { get; set; } = "https://openapi.yundaex.com/openapi/outer/upLoadWeight";
    }
}
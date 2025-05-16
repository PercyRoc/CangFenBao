using Common.Services.Settings;

namespace XinJuLi.Models.ASN
{
    /// <summary>
    /// ASN服务配置
    /// </summary>
    [Configuration("AsnSettings")]
    public class AsnSettings : BindableBase
    {
        private string _systemCode = "SH_FX";
        /// <summary>
        /// 系统编码
        /// </summary>
        public string SystemCode
        {
            get => _systemCode;
            set => SetProperty(ref _systemCode, value);
        }

        private string _houseCode = "SH_FX";
        /// <summary>
        /// 仓库编码
        /// </summary>
        public string HouseCode
        {
            get => _houseCode;
            set => SetProperty(ref _houseCode, value);
        }

        private string _httpServerUrl = "http://127.0.0.1:8080";
        /// <summary>
        /// HTTP服务监听地址
        /// </summary>
        public string HttpServerUrl
        {
            get => _httpServerUrl;
            set => SetProperty(ref _httpServerUrl, value);
        }

        private string _applicationName = "api";
        /// <summary>
        /// 应用名称
        /// </summary>
        public string ApplicationName
        {
            get => _applicationName;
            set => SetProperty(ref _applicationName, value);
        }

        private bool _isEnabled = true;
        /// <summary>
        /// 是否启用服务
        /// </summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        private string _reviewServerUrl = "";
        /// <summary>
        /// 扫码复核服务器地址
        /// </summary>
        public string ReviewServerUrl
        {
            get => _reviewServerUrl;
            set => SetProperty(ref _reviewServerUrl, value);
        }

        private string _reviewExitArea = "";
        /// <summary>
        /// 扫码复核月台
        /// </summary>
        public string ReviewExitArea
        {
            get => _reviewExitArea;
            set => SetProperty(ref _reviewExitArea, value);
        }
    }
} 
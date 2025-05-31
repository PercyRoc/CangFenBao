using Common.Services.Settings;

namespace Server.YouYu.Models
{
    [Configuration("YouYuConfig")]
    public class SegmentCodeUrlConfig : BindableBase
    {
        private string _url = string.Empty;
        private string _appId = string.Empty;
        private string _appKey = string.Empty;

        /// <summary>
        ///     接口地址
        /// </summary>
        public string Url
        {
            get => _url;
            set => SetProperty(ref _url, value);
        }

        /// <summary>
        ///     应用ID
        /// </summary>
        public string AppId
        {
            get => _appId;
            set => SetProperty(ref _appId, value);
        }

        /// <summary>
        ///     应用密钥
        /// </summary>
        public string AppKey
        {
            get => _appKey;
            set => SetProperty(ref _appKey, value);
        }
    }
} 
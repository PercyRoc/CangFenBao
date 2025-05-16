using Common.Services.Settings; // ISettingsService
// DelegateCommand
// BindableBase
using SowingWall.Models.Settings; // WangDianTongSettings
using Serilog;

namespace SowingWall.ViewModels.Settings
{
    public class WangDianTongSettingsViewModel : BindableBase
    {
        private readonly ISettingsService _settingsService;
        private WangDianTongSettings _settings;

        public WangDianTongSettingsViewModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;
            LoadSettings();
            SaveCommand = new DelegateCommand(SaveSettings, CanSaveSettings);
        }

        private string _sid;
        public string Sid
        {
            get => _sid;
            set
            {
                if (SetProperty(ref _sid, value))
                {
                    SaveCommand.RaiseCanExecuteChanged();
                }
            }
        }

        private string _appKey;
        public string AppKey
        {
            get => _appKey;
            set
            {
                if (SetProperty(ref _appKey, value))
                {
                    SaveCommand.RaiseCanExecuteChanged();
                }
            }
        }

        private string _appSecret;
        public string AppSecret
        {
            get => _appSecret;
            set
            {
                if (SetProperty(ref _appSecret, value))
                {
                    SaveCommand.RaiseCanExecuteChanged();
                }
            }
        }

        private string _requestUrl;
        public string RequestUrl
        {
            get => _requestUrl;
            set
            {
                if (SetProperty(ref _requestUrl, value))
                {
                    SaveCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public DelegateCommand SaveCommand { get; }

        private void LoadSettings()
        {
            try
            {
                _settings = _settingsService.LoadSettings<WangDianTongSettings>();
                Sid = _settings.Sid;
                AppKey = _settings.AppKey;
                AppSecret = _settings.AppSecret;
                RequestUrl = _settings.RequestUrl;
                SaveCommand.RaiseCanExecuteChanged(); 
            }
            catch (Exception ex)
            {
                Log.Error(ex, "加载旺店通设置失败");
                _settings = new WangDianTongSettings();
                Sid = _settings.Sid;
                AppKey = _settings.AppKey;
                AppSecret = _settings.AppSecret;
                RequestUrl = _settings.RequestUrl;
            }
        }

        private bool CanSaveSettings()
        {
            return (Sid != _settings.Sid || 
                    AppKey != _settings.AppKey || 
                    AppSecret != _settings.AppSecret ||
                    RequestUrl != _settings.RequestUrl);
        }

        private void SaveSettings()
        {
            _settings.Sid = Sid;
            _settings.AppKey = AppKey;
            _settings.AppSecret = AppSecret;
            _settings.RequestUrl = RequestUrl;

            try
            {
                _settingsService.SaveSettings(_settings);
                _settings = _settingsService.LoadSettings<WangDianTongSettings>();
                SaveCommand.RaiseCanExecuteChanged();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存旺店通设置失败");
                
            }
        }
    }
} 
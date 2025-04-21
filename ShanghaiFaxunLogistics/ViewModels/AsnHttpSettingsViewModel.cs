using System.ComponentModel.DataAnnotations;
using Common.Services.Settings;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using ShanghaiFaxunLogistics.Models.ASN;

namespace ShanghaiFaxunLogistics.ViewModels
{
    /// <summary>
    /// ASN HTTP服务设置视图模型
    /// </summary>
    public class AsnHttpSettingsViewModel : BindableBase
    {
        private readonly ISettingsService _settingsService;
        private AsnSettings _settings = new();

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="settingsService">设置服务</param>
        public AsnHttpSettingsViewModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;
            SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);
            LoadConfigurationCommand = new DelegateCommand(ExecuteLoadConfiguration);

            ExecuteLoadConfiguration();
        }

        /// <summary>
        /// 是否启用HTTP服务
        /// </summary>
        public bool IsEnabled
        {
            get => _settings.IsEnabled;
            set
            {
                _settings.IsEnabled = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// 系统编码
        /// </summary>
        [Required(ErrorMessage = "系统编码不能为空")]
        public string SystemCode
        {
            get => _settings.SystemCode;
            set
            {
                _settings.SystemCode = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// 仓库编码
        /// </summary>
        [Required(ErrorMessage = "仓库编码不能为空")]
        public string HouseCode
        {
            get => _settings.HouseCode;
            set
            {
                _settings.HouseCode = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// HTTP服务监听地址
        /// </summary>
        [Required(ErrorMessage = "HTTP服务地址不能为空")]
        public string HttpServerUrl
        {
            get => _settings.HttpServerUrl;
            set
            {
                _settings.HttpServerUrl = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// 应用名称
        /// </summary>
        [Required(ErrorMessage = "应用名称不能为空")]
        public string ApplicationName
        {
            get => _settings.ApplicationName;
            set
            {
                _settings.ApplicationName = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// 保存配置命令
        /// </summary>
        public DelegateCommand SaveConfigurationCommand { get; }

        /// <summary>
        /// 加载配置命令
        /// </summary>
        public DelegateCommand LoadConfigurationCommand { get; }

        /// <summary>
        /// 执行保存配置
        /// </summary>
        private void ExecuteSaveConfiguration()
        {
            try
            {
                Log.Information("保存ASN HTTP服务配置");
                _settingsService.SaveSettings(_settings);
                Log.Information("ASN HTTP服务配置已保存");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存ASN HTTP服务配置时发生错误");
            }
        }

        /// <summary>
        /// 执行加载配置
        /// </summary>
        private void ExecuteLoadConfiguration()
        {
            try
            {
                Log.Information("加载ASN HTTP服务配置");
                _settings = _settingsService.LoadSettings<AsnSettings>();
                Log.Information("ASN HTTP服务配置已加载");

                // 通知UI更新
                RaisePropertyChanged(nameof(IsEnabled));
                RaisePropertyChanged(nameof(SystemCode));
                RaisePropertyChanged(nameof(HouseCode));
                RaisePropertyChanged(nameof(HttpServerUrl));
                RaisePropertyChanged(nameof(ApplicationName));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "加载ASN HTTP服务配置时发生错误");
            }
        }
    }
}
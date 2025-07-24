using Common.Services.Settings;
using FuzhouPolicyForce.Models.Settings;
using HandyControl.Controls;
using Serilog;
using System.ComponentModel;
using System.Reflection;

namespace FuzhouPolicyForce.ViewModels.Settings
{
    public class EnvironmentOption
    {
        public AnttoWeightEnvironment Value { get; set; }
        public string DisplayName { get; set; } = string.Empty;
    }

    public class AnttoWeightSettingsViewModel : BindableBase
    {
        private readonly ISettingsService _settingsService;
        private AnttoWeightSettings _settings;

        public AnttoWeightSettingsViewModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;
            _settings = _settingsService.LoadSettings<AnttoWeightSettings>();

            SaveCommand = new DelegateCommand(ExecuteSaveCommand);
            InitializeAvailableEnvironments();
        }

        public AnttoWeightSettings Settings
        {
            get => _settings;
            set => SetProperty(ref _settings, value);
        }

        public List<EnvironmentOption> AvailableEnvironments { get; private set; } = new();

        public DelegateCommand SaveCommand { get; }

        private void InitializeAvailableEnvironments()
        {
            AvailableEnvironments.Clear();
            foreach (AnttoWeightEnvironment env in Enum.GetValues(typeof(AnttoWeightEnvironment)))
            {
                var displayName = GetEnumDescription(env);
                AvailableEnvironments.Add(new EnvironmentOption { Value = env, DisplayName = displayName });
            }
        }

        private static string GetEnumDescription(AnttoWeightEnvironment value)
        {
            var field = value.GetType().GetField(value.ToString());
            var attribute = field?.GetCustomAttribute<DescriptionAttribute>();
            return attribute?.Description ?? value.ToString();
        }

        private void ExecuteSaveCommand()
        {
            try
            {
                _settingsService.SaveSettings(Settings);
                Log.Information("安通称重API设置已保存。");
                Growl.Success("安通称重API设置已保存成功！");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存安通称重API设置时发生错误。");
                Growl.Error($"保存安通称重API设置失败: {ex.Message}");
            }
        }
    }
} 
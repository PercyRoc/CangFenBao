using System.Collections.ObjectModel;
using System.Windows.Input;
using Common.Services.Settings;
using Prism.Commands;
using Prism.Mvvm;
using SortingServices.Car;

namespace SharedUI.ViewModels
{
    /// <summary>
    /// 小车配置视图模型
    /// </summary>
    public class CarConfigViewModel : BindableBase
    {
        private readonly ISettingsService _settingsService;
        private readonly CarConfigModel _carConfigModel;
        private CarConfig? _selectedCarConfig;

        /// <summary>
        /// 小车配置集合
        /// </summary>
        public ObservableCollection<CarConfig>? CarConfigs => _carConfigModel.CarConfigs;

        /// <summary>
        /// 选中的小车配置
        /// </summary>
        public CarConfig? SelectedCarConfig
        {
            get => _selectedCarConfig;
            set => SetProperty(ref _selectedCarConfig, value);
        }

        /// <summary>
        /// 添加小车配置命令
        /// </summary>
        public ICommand AddCarConfigCommand { get; }

        /// <summary>
        /// 删除小车配置命令
        /// </summary>
        public ICommand DeleteCarConfigCommand { get; }

        /// <summary>
        /// 保存配置命令
        /// </summary>
        public DelegateCommand SaveConfigCommand { get; }

        public CarConfigViewModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;
            _carConfigModel = new CarConfigModel();
            
            AddCarConfigCommand = new DelegateCommand(AddCarConfig);
            DeleteCarConfigCommand = new DelegateCommand(DeleteCarConfig, CanDeleteCarConfig)
                .ObservesProperty(() => SelectedCarConfig);
            SaveConfigCommand = new DelegateCommand(SaveConfig);

            LoadConfig();
        }

        /// <summary>
        /// 加载配置
        /// </summary>
        private void LoadConfig()
        {
            var settings = _settingsService.LoadSettings<CarConfigModel>();
            // _carConfigModel is guaranteed non-null here.
            // _carConfigModel.CarConfigs is guaranteed non-null by CarConfigModel constructor.
            _carConfigModel.CarConfigs!.Clear(); // Clear existing items

            if (settings.CarConfigs != null)
            {
                foreach (var carConfig in settings.CarConfigs)
                {
                    _carConfigModel.CarConfigs.Add(carConfig); // Add loaded items
                }
            }
            
            // 初始化选中项
            if (CarConfigs?.Count > 0) // CarConfigs returns _carConfigModel.CarConfigs
            {
                SelectedCarConfig = CarConfigs[0];
            }
        }

        /// <summary>
        /// 保存配置
        /// </summary>
        private void SaveConfig()
        {
            _settingsService.SaveSettings(_carConfigModel);
        }

        /// <summary>
        /// 添加小车配置
        /// </summary>
        private void AddCarConfig()
        {
            if (CarConfigs == null) return;
            
            var newCarConfig = new CarConfig
            {
                Name = $"小车 {CarConfigs.Count + 1}",
                Address = (byte)(CarConfigs.Count + 1),
                Speed = 500,
                Acceleration = 6,
                Delay = 350,
                Time = 500
            };
            
            CarConfigs.Add(newCarConfig);
            SelectedCarConfig = newCarConfig;
        }

        /// <summary>
        /// 删除小车配置
        /// </summary>
        private void DeleteCarConfig()
        {
            if (SelectedCarConfig != null && CarConfigs != null)
            {
                CarConfigs.Remove(SelectedCarConfig);
                SelectedCarConfig = CarConfigs.Count > 0 ? CarConfigs[0] : null;
            }
        }

        /// <summary>
        /// 判断是否可以删除小车配置
        /// </summary>
        private bool CanDeleteCarConfig()
        {
            return SelectedCarConfig != null;
        }
    }
} 
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Common.Services.Settings;
using Sorting_Car.Models;
using Sorting_Car.Resources;

namespace Sorting_Car.ViewModels
{
    /// <summary>
    /// 小车分拣序列配置视图模型
    /// </summary>
    public class CarSequenceViewModel : BindableBase
    {
        private readonly ISettingsService _settingsService;
        private readonly CarSequenceSettings _carSequenceSettings;
        private readonly CarConfigModel _carConfigModel;

        private ChuteCarSequence? _selectedChute;
        private CarSequenceItem? _selectedCarItem;

        /// <summary>
        /// 格口集合
        /// </summary>
        public ObservableCollection<ChuteCarSequence> ChuteSequences => _carSequenceSettings.ChuteSequences;

        /// <summary>
        /// 可用小车配置
        /// </summary>
        public ObservableCollection<CarConfig>? AvailableCars => _carConfigModel.CarConfigs;

        /// <summary>
        /// 格口序列标题（本地化+格式化）
        /// </summary>
        public string ChuteSequenceTitle =>
            SelectedChute == null ? string.Empty :
                // 使用本地化资源格式化
                string.Format(Strings.CarSequence_ChuteSequenceTitle, SelectedChute.ChuteNumber);

        /// <summary>
        /// 选中的格口
        /// </summary>
        public ChuteCarSequence? SelectedChute
        {
            get => _selectedChute;
            set
            {
                // 取消订阅旧序列项的事件
                UnsubscribeFromSequenceItems();

                if (SetProperty(ref _selectedChute, value))
                {
                    RaisePropertyChanged(nameof(CarSequence));
                    // 订阅新序列项的事件
                    SubscribeToSequenceItems();
                    // 通知标题变更
                    RaisePropertyChanged(nameof(ChuteSequenceTitle));
                    // 更新删除格口命令的可执行状态
                    ((DelegateCommand)DeleteChuteCommand).RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>
        /// 选中的小车序列项
        /// </summary>
        public CarSequenceItem? SelectedCarItem
        {
            get => _selectedCarItem;
            set => SetProperty(ref _selectedCarItem, value);
        }

        /// <summary>
        /// 当前选中格口的小车序列
        /// </summary>
        public ObservableCollection<CarSequenceItem>? CarSequence => _selectedChute?.CarSequence;

        /// <summary>
        /// 添加小车命令
        /// </summary>
        public ICommand AddCarCommand { get; }

        /// <summary>
        /// 删除小车命令
        /// </summary>
        public ICommand RemoveCarCommand { get; }

        /// <summary>
        /// 小车正转命令
        /// </summary>
        public ICommand SetForwardCommand { get; }

        /// <summary>
        /// 小车反转命令
        /// </summary>
        public ICommand SetReverseCommand { get; }

        /// <summary>
        /// 添加格口命令
        /// </summary>
        public ICommand AddChuteCommand { get; }

        /// <summary>
        /// 删除格口命令
        /// </summary>
        public ICommand DeleteChuteCommand { get; }

        /// <summary>
        /// 异常出口号
        /// </summary>
        public int ExceptionChuteNumber
        {
            get => _carSequenceSettings.ExceptionChuteNumber;
            set
            {
                if (_carSequenceSettings.ExceptionChuteNumber == value) return;
                _carSequenceSettings.ExceptionChuteNumber = value;
                RaisePropertyChanged();
                SaveSettings(); // 当属性更改时保存设置
            }
        }

        public CarSequenceViewModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;

            // 加载小车配置
            _carConfigModel = _settingsService.LoadSettings<CarConfigModel>();

            // 加载小车序列设置
            _carSequenceSettings = _settingsService.LoadSettings<CarSequenceSettings>();
            
            // 如果 ChuteSequences 为 null（例如，首次加载或配置文件损坏），则初始化

            // 初始化命令
            AddCarCommand = new DelegateCommand<CarConfig>(ExecuteAddCar, CanAddCar);
            RemoveCarCommand = new DelegateCommand(ExecuteRemoveCar, CanRemoveCar)
                .ObservesProperty(() => SelectedCarItem);
            SetForwardCommand = new DelegateCommand(ExecuteSetForward, CanSetDirection)
                .ObservesProperty(() => SelectedCarItem);
            SetReverseCommand = new DelegateCommand(ExecuteSetReverse, CanSetDirection)
                .ObservesProperty(() => SelectedCarItem);

            AddChuteCommand = new DelegateCommand(ExecuteAddChute);
            DeleteChuteCommand = new DelegateCommand(ExecuteDeleteChute, CanDeleteChute)
                .ObservesProperty(() => SelectedChute);

            // 选择第一个格口（如果存在）
            if (ChuteSequences.Any())
            {
                SelectedChute = ChuteSequences[0]; 
            }
            // 订阅格口集合变化，以便在添加/删除格口时更新订阅
            ChuteSequences.CollectionChanged += OnChuteSequencesChanged;
        }
        
        private void OnChuteSequencesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // 如果是添加操作，并且新添加的项是 SelectedChute（例如，添加第一个格口时）
            // 或者如果是重置操作，并且 SelectedChute 不再有效
            if ((e is not { Action: NotifyCollectionChangedAction.Add, NewItems: not null } ||
                 !e.NewItems.Contains(SelectedChute)) &&
                (e.Action != NotifyCollectionChangedAction.Reset ||
                 (SelectedChute != null && ChuteSequences.Contains(SelectedChute)))) return;
            // 确保订阅了正确的 SelectedChute
            if (SelectedChute != null)
            {
                SubscribeToSequenceItems();
            }
        }

        /// <summary>
        /// 添加新格口
        /// </summary>
        private void ExecuteAddChute()
        {
            int nextChuteNumber = 1;
            if (ChuteSequences.Any())
            {
                nextChuteNumber = ChuteSequences.Max(c => c.ChuteNumber) + 1;
            }

            var newChute = new ChuteCarSequence
            {
                ChuteNumber = nextChuteNumber,
                CarSequence = new ObservableCollection<CarSequenceItem>()
            };
            ChuteSequences.Add(newChute);
            SelectedChute = newChute; // 自动选中新添加的格口
            SaveSettings();
        }

        /// <summary>
        /// 删除选中的格口
        /// </summary>
        private void ExecuteDeleteChute()
        {
            if (SelectedChute == null) return;

            int selectedIndex = ChuteSequences.IndexOf(SelectedChute);
            ChuteSequences.Remove(SelectedChute);

            if (ChuteSequences.Any())
            {
                if (selectedIndex >= ChuteSequences.Count) // 如果删除了最后一个
                {
                    SelectedChute = ChuteSequences.Last();
                }
                else // 选择相同索引或新的最后一个
                {
                    SelectedChute = ChuteSequences[selectedIndex];
                }
            }
            else
            {
                SelectedChute = null;
            }
            SaveSettings();
        }

        private bool CanDeleteChute()
        {
            return SelectedChute != null;
        }

        /// <summary>
        /// 添加小车到当前格口序列
        /// </summary>
        private void ExecuteAddCar(CarConfig? car)
        {
            if (SelectedChute == null || car == null) return;

            var carItem = new CarSequenceItem
            {
                CarAddress = car.Address,
                CarName = car.Name,
                IsReverse = false,
                DelayMs = 0 
            };
            SelectedChute.CarSequence.Add(carItem);
            SelectedCarItem = carItem;
            SaveSettings();
        }

        private bool CanAddCar(CarConfig? car)
        {
            return SelectedChute != null && car != null;
        }

        /// <summary>
        /// 从当前格口序列移除选中的小车
        /// </summary>
        private void ExecuteRemoveCar()
        {
            if (SelectedChute == null || SelectedCarItem == null) return;

            SelectedChute.CarSequence.Remove(SelectedCarItem);
            SelectedCarItem = SelectedChute.CarSequence.Any() ? SelectedChute.CarSequence.First() : null;
            SaveSettings();
        }

        private bool CanRemoveCar()
        {
            return SelectedChute != null && SelectedCarItem != null;
        }

        /// <summary>
        /// 设置选中小车为正转
        /// </summary>
        private void ExecuteSetForward()
        {
            if (SelectedCarItem == null) return;
            SelectedCarItem.IsReverse = false;
        }

        /// <summary>
        /// 设置选中小车为反转
        /// </summary>
        private void ExecuteSetReverse()
        {
            if (SelectedCarItem == null) return;
            SelectedCarItem.IsReverse = true;
        }

        private bool CanSetDirection()
        {
            return SelectedCarItem != null;
        }

        private void SaveSettings()
        {
            _settingsService.SaveSettings(_carSequenceSettings);
        }

        private void SubscribeToSequenceItems()
        {
            if (CarSequence == null) return;
            CarSequence.CollectionChanged += OnCarSequenceCollectionChanged;
            foreach (var item in CarSequence)
            {
                item.PropertyChanged += OnCarSequenceItemPropertyChanged;
            }
        }

        private void UnsubscribeFromSequenceItems()
        {
            if (CarSequence == null) return;
            CarSequence.CollectionChanged -= OnCarSequenceCollectionChanged;
            foreach (var item in CarSequence)
            {
                item.PropertyChanged -= OnCarSequenceItemPropertyChanged;
            }
        }

        private void OnCarSequenceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (var item in e.OldItems.OfType<CarSequenceItem>())
                {
                    item.PropertyChanged -= OnCarSequenceItemPropertyChanged;
                }
            }
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems.OfType<CarSequenceItem>())
                {
                    item.PropertyChanged += OnCarSequenceItemPropertyChanged;
                }
            }
            // 如果集合变为空，则 SelectedCarItem 应为 null
            if (CarSequence != null && !CarSequence.Any())
            {
                SelectedCarItem = null;
            }
        }

        private void OnCarSequenceItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CarSequenceItem.DelayMs) || e.PropertyName == nameof(CarSequenceItem.IsReverse))
            {
                SaveSettings();
            }
        }
    }
} 
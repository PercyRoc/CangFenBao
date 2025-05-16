using System.Collections.ObjectModel;
using Common.Services.Settings;

namespace Sorting_Car.Models
{
    /// <summary>
    /// 小车序列项，表示序列中的一个小车配置
    /// </summary>
    public class CarSequenceItem : BindableBase
    {
        private byte _carAddress;
        private bool _isReverse;
        private int _delayMs;
        
        /// <summary>
        /// 小车地址
        /// </summary>
        public byte CarAddress
        {
            get => _carAddress;
            set => SetProperty(ref _carAddress, value);
        }
        
        /// <summary>
        /// 是否反转
        /// </summary>
        public bool IsReverse
        {
            get => _isReverse;
            set => SetProperty(ref _isReverse, value);
        }
        
        /// <summary>
        /// 发送此小车命令前的延迟时间（毫秒）
        /// </summary>
        public int DelayMs
        {
            get => _delayMs;
            set => SetProperty(ref _delayMs, value);
        }
        
        /// <summary>
        /// 小车名称（用于显示）
        /// </summary>
        public string CarName { get; set; } = string.Empty;
    }
    
    /// <summary>
    /// 格口对应的小车序列配置
    /// </summary>
    public class ChuteCarSequence : BindableBase
    {
        private int _chuteNumber;
        private ObservableCollection<CarSequenceItem> _carSequence = [];
        
        /// <summary>
        /// 格口号
        /// </summary>
        public int ChuteNumber
        {
            get => _chuteNumber;
            set => SetProperty(ref _chuteNumber, value);
        }
        
        /// <summary>
        /// 小车序列
        /// </summary>
        public ObservableCollection<CarSequenceItem> CarSequence
        {
            get => _carSequence;
            set => SetProperty(ref _carSequence, value);
        }
    }
    
    /// <summary>
    /// 小车分拣序列设置
    /// </summary>
    [Configuration("CarSequenceSettings")]
    public class CarSequenceSettings : BindableBase
    {
        private ObservableCollection<ChuteCarSequence> _chuteSequences = new();
        
        /// <summary>
        /// 格口小车序列集合
        /// </summary>
        public ObservableCollection<ChuteCarSequence> ChuteSequences
        {
            get => _chuteSequences;
            set => SetProperty(ref _chuteSequences, value);
        }
        
        /// <summary>
        /// 初始化指定数量的格口
        /// </summary>
        /// <param name="chuteCount">格口数量</param>
        public void InitializeChutes(int chuteCount)
        {
            // 保留已有的格口配置
            var existingChutes = new Dictionary<int, ChuteCarSequence>();
            foreach (var sequence in ChuteSequences)
            {
                existingChutes[sequence.ChuteNumber] = sequence;
            }
            
            // 重新创建格口集合
            ChuteSequences.Clear();
            
            // 添加格口配置
            for (int i = 1; i <= chuteCount; i++)
            {
                if (existingChutes.TryGetValue(i, out var existingSequence))
                {
                    // 使用已有配置
                    ChuteSequences.Add(existingSequence);
                }
                else
                {
                    // 创建新配置
                    ChuteSequences.Add(new ChuteCarSequence
                    {
                        ChuteNumber = i,
                        CarSequence = []
                    });
                }
            }
        }
    }
} 
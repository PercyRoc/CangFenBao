using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using Prism.Commands;
using Prism.Mvvm;

namespace Common.ViewModels
{
    public class MainWindowViewModel : BindableBase
    {
        private readonly DispatcherTimer _timer;
        
        // 实时监控数据
        private int _currentSortingSpeed = 4520;
        private int _currentShiftCount = 23840;
        private int _errorPackageCount = 14;
        private string _runningTime = "6:24:18";
        private double _errorRate = 0.06;
        
        // 当前包裹信息
        private string _currentPackageId = "JD-7845-20230530";
        private string _currentWeight = "1.24";
        private string _currentSize = "25×18×12";
        private string _currentDestination = "上海市浦东新区";
        private string _currentBarcode = "JD-7845-20230530";
        private DateTime _scanTime = DateTime.Now;
        private string _targetSlot = "B-12 (华东区)";
        private string _expectedArrival = "2023-06-01 14:00前";
        private string _sender = "北京市海淀区";
        private string _courier = "京东物流";
        private string _packageStatus = "已分拣";
        
        // 识别结果
        private string _recognitionAccuracy = "98.7%";
        private string _recognitionStatus = "地址无法识别";
        private string _barcodeRecognition = "成功 (JD784520230530)";
        private string _weightDetection = "1.24 kg";
        private string _sizeDetection = "25×18×12 cm";
        private string _targetSlotResult = "B-12 (华东区)";
        private string _processingAdvice = "转人工分拣台";
        
        // 命令
        public ICommand RefreshCommand { get; }
        public ICommand PauseSortingCommand { get; }
        public ICommand EmergencyStopCommand { get; }
        public ICommand SettingsCommand { get; }
        public ICommand HistoryCommand { get; }
        public ICommand ExportDataCommand { get; }
        public ICommand FullScreenCommand { get; }
        
        // 历史记录
        public ObservableCollection<PackageHistoryItem> HistoryRecords { get; }
        
        public MainWindowViewModel()
        {
            // 初始化命令
            RefreshCommand = new DelegateCommand(OnRefresh);
            PauseSortingCommand = new DelegateCommand(OnPauseSorting);
            EmergencyStopCommand = new DelegateCommand(OnEmergencyStop);
            SettingsCommand = new DelegateCommand(OnSettings);
            HistoryCommand = new DelegateCommand(OnHistory);
            ExportDataCommand = new DelegateCommand(OnExportData);
            FullScreenCommand = new DelegateCommand(OnFullScreen);
            
            // 初始化历史记录
            HistoryRecords = new ObservableCollection<PackageHistoryItem>
            {
                new PackageHistoryItem("2023-05-30 10:25:18", "SF-9876-54321", "SF987654321", "0.85 kg", "20×15×8 cm", "上海市浦东新区", "A-08", "成功"),
                new PackageHistoryItem("2023-05-30 10:24:55", "JD-1234-56789", "JD123456789", "1.24 kg", "25×18×12 cm", "地址无法识别", "-", "异常"),
                new PackageHistoryItem("2023-05-30 10:24:32", "ST-5555-88888", "ST555588888", "2.10 kg", "30×25×15 cm", "北京市海淀区", "C-03", "成功"),
                new PackageHistoryItem("2023-05-30 10:24:10", "YT-7777-22222", "YT777722222", "5.60 kg", "40×30×25 cm", "广州市天河区", "B-15", "成功"),
                new PackageHistoryItem("2023-05-30 10:23:45", "ZM-4444-11111", "ZM444411111", "0.75 kg", "18×12×6 cm", "杭州市西湖区", "A-05", "成功"),
                new PackageHistoryItem("2023-05-30 10:23:22", "DB-9999-00000", "DB999900000", "1.80 kg", "28×20×15 cm", "成都市武侯区", "D-07", "成功"),
                new PackageHistoryItem("2023-05-30 10:22:58", "SF-1122-3344", "SF11223344", "3.20 kg", "35×25×20 cm", "南京市鼓楼区", "B-08", "成功"),
                new PackageHistoryItem("2023-05-30 10:22:35", "JD-5566-7788", "JD55667788", "0.95 kg", "22×16×9 cm", "武汉市武昌区", "C-11", "成功"),
                new PackageHistoryItem("2023-05-30 10:22:10", "ST-3344-5566", "ST33445566", "4.50 kg", "42×30×25 cm", "西安市雁塔区", "D-03", "成功")
            };
            
            // 启动定时器模拟实时数据更新
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _timer.Tick += OnTimerTick;
            _timer.Start();
        }
        
        // 属性
        public int CurrentSortingSpeed
        {
            get => _currentSortingSpeed;
            set => SetProperty(ref _currentSortingSpeed, value);
        }
        
        public int CurrentShiftCount
        {
            get => _currentShiftCount;
            set => SetProperty(ref _currentShiftCount, value);
        }
        
        public int ErrorPackageCount
        {
            get => _errorPackageCount;
            set => SetProperty(ref _errorPackageCount, value);
        }
        
        public string RunningTime
        {
            get => _runningTime;
            set => SetProperty(ref _runningTime, value);
        }
        
        public double ErrorRate
        {
            get => _errorRate;
            set => SetProperty(ref _errorRate, value);
        }
        
        public string CurrentPackageId
        {
            get => _currentPackageId;
            set => SetProperty(ref _currentPackageId, value);
        }
        
        public string CurrentWeight
        {
            get => _currentWeight;
            set => SetProperty(ref _currentWeight, value);
        }
        
        public string CurrentSize
        {
            get => _currentSize;
            set => SetProperty(ref _currentSize, value);
        }
        
        public string CurrentDestination
        {
            get => _currentDestination;
            set => SetProperty(ref _currentDestination, value);
        }
        
        public string CurrentBarcode
        {
            get => _currentBarcode;
            set => SetProperty(ref _currentBarcode, value);
        }
        
        public DateTime ScanTime
        {
            get => _scanTime;
            set => SetProperty(ref _scanTime, value);
        }
        
        public string TargetSlot
        {
            get => _targetSlot;
            set => SetProperty(ref _targetSlot, value);
        }
        
        public string ExpectedArrival
        {
            get => _expectedArrival;
            set => SetProperty(ref _expectedArrival, value);
        }
        
        public string Sender
        {
            get => _sender;
            set => SetProperty(ref _sender, value);
        }
        
        public string Courier
        {
            get => _courier;
            set => SetProperty(ref _courier, value);
        }
        
        public string PackageStatus
        {
            get => _packageStatus;
            set => SetProperty(ref _packageStatus, value);
        }
        
        public string RecognitionAccuracy
        {
            get => _recognitionAccuracy;
            set => SetProperty(ref _recognitionAccuracy, value);
        }
        
        public string RecognitionStatus
        {
            get => _recognitionStatus;
            set => SetProperty(ref _recognitionStatus, value);
        }
        
        public string BarcodeRecognition
        {
            get => _barcodeRecognition;
            set => SetProperty(ref _barcodeRecognition, value);
        }
        
        public string WeightDetection
        {
            get => _weightDetection;
            set => SetProperty(ref _weightDetection, value);
        }
        
        public string SizeDetection
        {
            get => _sizeDetection;
            set => SetProperty(ref _sizeDetection, value);
        }
        
        public string TargetSlotResult
        {
            get => _targetSlotResult;
            set => SetProperty(ref _targetSlotResult, value);
        }
        
        public string ProcessingAdvice
        {
            get => _processingAdvice;
            set => SetProperty(ref _processingAdvice, value);
        }
        
        // 私有方法
        private void OnTimerTick(object sender, EventArgs e)
        {
            // 模拟实时数据更新
            var random = new Random();
            
            // 更新分拣速度
            CurrentSortingSpeed += random.Next(-10, 21);
            if (CurrentSortingSpeed < 0) CurrentSortingSpeed = 0;
            
            // 更新分拣量
            CurrentShiftCount += random.Next(0, 11);
            
            // 更新运行时间
            var time = DateTime.Parse(_runningTime);
            time = time.AddSeconds(2);
            RunningTime = time.ToString("H:mm:ss");
            
            // 随机更新包裹信息
            if (random.NextDouble() > 0.8)
            {
                var carriers = new[] { "SF", "JD", "ST", "YT", "ZM", "DB" };
                var carrier = carriers[random.Next(carriers.Length)];
                CurrentPackageId = $"{carrier}-{random.Next(1000, 10000)}-{DateTime.Now:yyyyMMdd}";
                CurrentBarcode = CurrentPackageId;
                CurrentWeight = (random.NextDouble() * 5 + 0.1).ToString("F2");
                
                var width = random.Next(15, 36);
                var height = random.Next(10, 26);
                var depth = random.Next(5, 16);
                CurrentSize = $"{width}×{height}×{depth}";
                
                ScanTime = DateTime.Now;
            }
            
            // 随机添加异常包裹
            if (random.NextDouble() > 0.9)
            {
                ErrorPackageCount++;
                ErrorRate = (double)ErrorPackageCount / CurrentShiftCount * 100;
            }
        }
        
        private void OnRefresh()
        {
            // 刷新数据逻辑
        }
        
        private void OnPauseSorting()
        {
            // 暂停分拣逻辑
        }
        
        private void OnEmergencyStop()
        {
            // 紧急停止逻辑
        }
        
        private void OnSettings()
        {
            // 打开设置页面逻辑
        }
        
        private void OnHistory()
        {
            // 打开历史记录页面逻辑
        }
        
        private void OnExportData()
        {
            // 导出数据逻辑
        }
        
        private void OnFullScreen()
        {
            // 全屏逻辑
        }
    }
    
    public class PackageHistoryItem
    {
        public string Time { get; set; }
        public string PackageId { get; set; }
        public string Barcode { get; set; }
        public string Weight { get; set; }
        public string Size { get; set; }
        public string Destination { get; set; }
        public string Slot { get; set; }
        public string Status { get; set; }
        
        public PackageHistoryItem(string time, string packageId, string barcode, string weight, string size, string destination, string slot, string status)
        {
            Time = time;
            PackageId = packageId;
            Barcode = barcode;
            Weight = weight;
            Size = size;
            Destination = destination;
            Slot = slot;
            Status = status;
        }
    }
} 
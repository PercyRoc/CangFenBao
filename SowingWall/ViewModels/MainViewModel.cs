using System.Collections.ObjectModel;
using System.Windows;
using Common.Models.Package;
using Serilog;

namespace SowingWall.ViewModels
{
    public class SowingCellViewModel : BindableBase
    {
        private string _cellNumber;
        public string CellNumber
        {
            get => _cellNumber;
            set => SetProperty(ref _cellNumber, value);
        }

        private SowingCellStatus _status;
        public SowingCellStatus Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        private string _quantityDisplay;
        public string QuantityDisplay
        {
            get => _quantityDisplay;
            set => SetProperty(ref _quantityDisplay, value);
        }

        private bool _isCurrentTarget;
        public bool IsCurrentTarget
        {
            get => _isCurrentTarget;
            set => SetProperty(ref _isCurrentTarget, value);
        }

        public SowingCellViewModel(string cellNumber, SowingCellStatus status = SowingCellStatus.Empty, string quantity = "0/0")
        {
            CellNumber = cellNumber;
            Status = status;
            QuantityDisplay = quantity;
        }
    }

    public enum SowingCellStatus
    {
        Empty,
        PendingPut,
        Completed,
        Error,
        Full
    }

    public class MainViewModel : BindableBase
    {
        private readonly IDialogService _dialogService;

        #region Window Commands
        public DelegateCommand MinimizeWindowCommand { get; }
        public DelegateCommand MaximizeRestoreWindowCommand { get; }
        public DelegateCommand CloseWindowCommand { get; }
        #endregion

        #region Navigation/Action Commands
        public DelegateCommand OpenHistoryCommand { get; }
        public DelegateCommand OpenSettingsCommand { get; }
        public DelegateCommand ClearBarcodeCommand { get; }
        public DelegateCommand ConfirmInputCommand { get; }
        public DelegateCommand QuantityErrorReportCommand { get; }
        public DelegateCommand CellFullReportCommand { get; }
        public DelegateCommand SkipItemCommand { get; }
        public DelegateCommand EndTaskCommand { get; }
        #endregion

        #region Properties
        private string _waveIdText;
        public string WaveIdText
        {
            get => _waveIdText;
            set => SetProperty(ref _waveIdText, value, OnWaveIdTextChanged);
        }

        private string _barcodeText;
        public string BarcodeText
        {
            get => _barcodeText;
            set => SetProperty(ref _barcodeText, value, OnBarcodeTextChanged);
        }

        private string _instructionText = "请先扫描或输入波次号";
        public string InstructionText
        {
            get => _instructionText;
            set => SetProperty(ref _instructionText, value);
        }

        private ObservableCollection<SowingCellViewModel> _sowingCells;
        public ObservableCollection<SowingCellViewModel> SowingCells
        {
            get => _sowingCells;
            set => SetProperty(ref _sowingCells, value);
        }

        private PackageInfo _currentPackage;
        public PackageInfo CurrentPackage
        {
            get => _currentPackage;
            set
            {
                _currentPackage?.ReleaseImage();
                SetProperty(ref _currentPackage, value);
                ConfirmInputCommand.RaiseCanExecuteChanged();
                QuantityErrorReportCommand.RaiseCanExecuteChanged();
                CellFullReportCommand.RaiseCanExecuteChanged();
                SkipItemCommand.RaiseCanExecuteChanged();
            }
        }

        private string _currentTaskInfo = "当前任务: P000012345 - 波次: B001";
        public string CurrentTaskInfo
        {
            get => _currentTaskInfo;
            set => SetProperty(ref _currentTaskInfo, value);
        }


        private int _taskProgress;
        public int TaskProgress
        {
            get => _taskProgress;
            set => SetProperty(ref _taskProgress, value);
        }

        private int _totalScanned;
        public int TotalScanned
        {
            get => _totalScanned;
            set => SetProperty(ref _totalScanned, value);
        }

        private int _totalExceptions;
        public int TotalExceptions
        {
            get => _totalExceptions;
            set => SetProperty(ref _totalExceptions, value);
        }
        #endregion

        public MainViewModel(IDialogService dialogService /*, ISettingsService settingsService */)
        {
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            // _settingsService = settingsService;

            MinimizeWindowCommand = new DelegateCommand(ExecuteMinimizeWindow);
            MaximizeRestoreWindowCommand = new DelegateCommand(ExecuteMaximizeRestoreWindow);
            CloseWindowCommand = new DelegateCommand(ExecuteCloseWindow);

            OpenHistoryCommand = new DelegateCommand(ExecuteOpenHistory);
            OpenSettingsCommand = new DelegateCommand(ExecuteOpenSettings);
            ClearBarcodeCommand = new DelegateCommand(ExecuteClearBarcode);

            ConfirmInputCommand = new DelegateCommand(ExecuteConfirmInput, CanExecuteConfirmInput);
            QuantityErrorReportCommand = new DelegateCommand(ExecuteQuantityErrorReport, CanExecuteActionCommands);
            CellFullReportCommand = new DelegateCommand(ExecuteCellFullReport, CanExecuteActionCommands);
            SkipItemCommand = new DelegateCommand(ExecuteSkipItem, CanExecuteActionCommands);
            EndTaskCommand = new DelegateCommand(ExecuteEndTask);

            LoadSowingCells(); 
            LoadSamplePackage();
            UpdateTaskStatistics();
        }

        private void OnWaveIdTextChanged()
        {
            if (!string.IsNullOrWhiteSpace(WaveIdText) && string.IsNullOrWhiteSpace(BarcodeText))
            {
                InstructionText = $"波次号: {WaveIdText}. 请扫描商品SKU。";
            }
            else if (string.IsNullOrWhiteSpace(WaveIdText))
            {
                InstructionText = "请先扫描或输入波次号";
            }
            ConfirmInputCommand.RaiseCanExecuteChanged();
        }

        private void OnBarcodeTextChanged()
        {
            if (string.IsNullOrWhiteSpace(WaveIdText))
            {
                InstructionText = "请先扫描或输入波次号，再扫描SKU。";
                return;
            }

            if (!string.IsNullOrWhiteSpace(BarcodeText))
            {
                InstructionText = $"波次号: {WaveIdText}, SKU: {BarcodeText}. 请确认或下一步操作。";
                if (CurrentPackage != null && BarcodeText != CurrentPackage.Barcode)
                {
                }
            }
            else
            {
                InstructionText = $"波次号: {WaveIdText}. 请扫描商品SKU。";
            }
            ConfirmInputCommand.RaiseCanExecuteChanged();
        }

        #region Command Implementations
        private void ExecuteMinimizeWindow() 
        {
            if (Application.Current.MainWindow != null) 
                Application.Current.MainWindow.WindowState = WindowState.Minimized;
        }

        private void ExecuteMaximizeRestoreWindow()
        {
            if (Application.Current.MainWindow != null)
                Application.Current.MainWindow.WindowState = Application.Current.MainWindow.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void ExecuteCloseWindow()
        {
            Application.Current.MainWindow?.Close();
        }

        private void ExecuteOpenHistory()
        {
           _dialogService.ShowDialog("HistoryDialogView");
        }

        private void ExecuteOpenSettings()
        {
            _dialogService.ShowDialog("SettingsDialog");
        }

        private void ExecuteClearBarcode()
        {
            BarcodeText = string.Empty; 
            Log.Information("SKU (BarcodeText) cleared.");
        }

        private bool CanExecuteConfirmInput() => !string.IsNullOrWhiteSpace(WaveIdText) && !string.IsNullOrWhiteSpace(BarcodeText) && CurrentPackage != null && BarcodeText == CurrentPackage.Barcode;
        private void ExecuteConfirmInput()
        {
            Log.Information("ConfirmInput for WaveID: {WaveId}, SKU: {Barcode}", WaveIdText, CurrentPackage?.Barcode);
            InstructionText = $"波次 {WaveIdText}, 商品 {CurrentPackage?.Barcode} 确认投入。";
            
            var targetCell = SowingCells.FirstOrDefault(c => c.IsCurrentTarget && c.Status == SowingCellStatus.PendingPut);
            if (targetCell != null) 
            { 
                targetCell.Status = SowingCellStatus.Completed; 
                targetCell.QuantityDisplay = "1/1"; // Example update
                targetCell.IsCurrentTarget = false; 
                Log.Information("Cell {CellNumber} status updated to Completed.", targetCell.CellNumber);
            }
            else
            {
                Log.Warning("No current target cell in PendingPut status found for confirmation.");
                InstructionText = $"错误: 未找到待投入的目标格口 for {CurrentPackage?.Barcode}。";
            }
            
            BarcodeText = string.Empty; // Clear SKU for next scan
            TotalScanned++;
            UpdateTaskStatistics();

            var nextCell = SowingCells.FirstOrDefault(c => c.Status == SowingCellStatus.PendingPut || c.Status == SowingCellStatus.Empty);
            if (nextCell != null)
            {
                nextCell.IsCurrentTarget = true;
                if(nextCell.Status == SowingCellStatus.Empty) nextCell.Status = SowingCellStatus.PendingPut;
                 Log.Information("Next target cell set to {CellNumber}.", nextCell.CellNumber);
            }
            else
            {
                Log.Information("No more pending cells found. Task might be complete.");
                InstructionText = "所有格口处理完毕或无待处理格口。";
            }
        }

        private bool CanExecuteActionCommands() => !string.IsNullOrWhiteSpace(WaveIdText) && CurrentPackage != null && !string.IsNullOrWhiteSpace(BarcodeText);

        private void ExecuteQuantityErrorReport()
        {
            Log.Warning("Quantity Error Reported for WaveID: {WaveId}, SKU: {Barcode}", WaveIdText, CurrentPackage?.Barcode);
            InstructionText = $"波次 {WaveIdText}, 包裹 {CurrentPackage?.Barcode} 数量异常已报告。";
            TotalExceptions++;
            UpdateTaskStatistics();
            var targetCell = SowingCells.FirstOrDefault(c => c.IsCurrentTarget);
            if (targetCell != null)
            {
                targetCell.Status = SowingCellStatus.Error;
            }
        }

        private void ExecuteCellFullReport()
        {
            Log.Warning("Cell Full Reported while handling WaveID: {WaveId}, SKU: {Barcode}", WaveIdText, CurrentPackage?.Barcode);
            InstructionText = $"格口已满报告 (波次: {WaveIdText}, 包裹: {CurrentPackage?.Barcode})。";
            TotalExceptions++;
            UpdateTaskStatistics();
            var targetCell = SowingCells.FirstOrDefault(c => c.IsCurrentTarget);
            if (targetCell != null)
            {
                targetCell.Status = SowingCellStatus.Full;
                 Log.Information("Cell {CellNumber} status updated to Full.", targetCell.CellNumber);
            }
        }

        private void ExecuteSkipItem()
        {
            Log.Information("Skipping item: WaveID: {WaveId}, SKU: {Barcode}", WaveIdText, CurrentPackage?.Barcode);
            InstructionText = $"已跳过波次 {WaveIdText}, 商品 {CurrentPackage?.Barcode}。";
            BarcodeText = string.Empty; // Clear SKU, WaveIdText might persist
        }

        private void ExecuteEndTask()
        {
            Log.Information("End Task requested.");
            InstructionText = "任务结束流程启动...";
            var parameters = new DialogParameters("message=您确定要结束当前播种任务吗？");
            _dialogService.ShowDialog("ConfirmationDialog", parameters, r => 
            {
                if (r.Result == ButtonResult.OK)
                {
                    Log.Information("User confirmed to end task. Performing cleanup...");
                    InstructionText = "任务已结束。";
                }
                else
                {
                    Log.Information("User cancelled ending task.");
                    InstructionText = "任务结束已取消。";
                }
            });
        }
        #endregion

        #region Data Loading / Helper methods
        private void LoadSowingCells()
        {
            SowingCells = new ObservableCollection<SowingCellViewModel>();
            int rows = 8;
            int columns = 10;
            for (int i = 1; i <= rows * columns; i++) 
            {
                SowingCells.Add(new SowingCellViewModel($"G{i:D3}"));
            }
            
            if (SowingCells.Any())
            {
                SowingCells[0].IsCurrentTarget = true;
                SowingCells[0].Status = SowingCellStatus.PendingPut;
                SowingCells[0].QuantityDisplay = "0/1";
            }
            Log.Debug("播种单元格已加载。数量: {Count}", SowingCells.Count);
        }

        private void LoadSamplePackage()
        {
            Log.Debug("Attempting to load sample package...");
            try
            {
                PackageInfo samplePackage = null;
                
                if (samplePackage == null)
                {
                    Log.Error("Failed to initialize PackageInfo instance. Cannot load sample package.");
                    CurrentPackage = null;
                    return;
                }

                samplePackage.SetBarcode("1234567890123");
                samplePackage.SetWeight(1.25);
                samplePackage.SetDimensions(10, 10, 5);
                
                samplePackage.SetStatus(PackageStatus.Created);

                CurrentPackage = samplePackage;
                Log.Debug("Sample package loaded: {Barcode}", CurrentPackage.Barcode);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to create or load sample package.");
                CurrentPackage = null;
            }
        }

        private void UpdateTaskStatistics()
        {
            if (SowingCells == null || !SowingCells.Any())
            {
                TaskProgress = 0;
            }
            else
            {
                int completedCells = SowingCells.Count(c => c.Status == SowingCellStatus.Completed);
                TaskProgress = (int)Math.Round((double)completedCells * 100 / SowingCells.Count);
            }
            TaskProgress = Math.Min(Math.Max(0, TaskProgress), 100);
            
            Log.Information("Task statistics updated. Progress: {Progress}%, Scanned: {Scanned}, Exceptions: {Exceptions}", TaskProgress, TotalScanned, TotalExceptions);
        }
        #endregion
    }
}
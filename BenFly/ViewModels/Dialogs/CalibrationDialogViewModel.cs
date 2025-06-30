using System.Collections.ObjectModel;
using System.Windows;
using Common.Models.Settings.Sort.PendulumSort;
using Common.Services.Settings;
using JetBrains.Annotations;
using Serilog;
using SortingServices.Pendulum;

namespace BenFly.ViewModels.Dialogs;

public class CalibrationDialogViewModel : BindableBase, IDialogAware
{
    private readonly ISettingsService _settingsService;
    private readonly IPendulumSortService _sortService;
    private readonly PendulumSortConfig _config;
    
    private string _title = "分拣时间标定";
    private DateTime? _triggerTime;
    private DateTime? _sortingTime;
    private double _measuredDelay;
    private string _statusMessage = "等待触发光电信号...";
    private bool _isCalibrationMode;
    private string _selectedPhotoelectric = "触发光电";
    
    // 配置参数
    private double _timeRangeLower = 1000;
    private double _timeRangeUpper = 3000;
    private double _sortingDelay = 50;
    private double _resetDelay = 1000;

    public CalibrationDialogViewModel(ISettingsService settingsService, IPendulumSortService sortService)
    {
        _settingsService = settingsService;
        _sortService = sortService;
        
        // 加载当前配置
        _config = _settingsService.LoadSettings<PendulumSortConfig>();
        
        // 初始化可用光电列表
        InitializePhotoelectrics();
        
        // 加载当前选中光电的配置
        LoadSelectedPhotoelectricConfig();
        
        // 初始化命令
        ToggleCalibrationModeCommand = new DelegateCommand(() => IsCalibrationMode = !IsCalibrationMode);
        ApplyRecommendedSettingsCommand = new DelegateCommand(ExecuteApplyRecommendedSettings, CanApplyRecommendedSettings);
        SaveCommand = new DelegateCommand(ExecuteSave);
        CancelCommand = new DelegateCommand(ExecuteCancel);

        // 订阅光电信号事件
        _sortService.TriggerPhotoelectricSignal += OnTriggerPhotoelectricSignal;
        _sortService.SortingPhotoelectricSignal += OnSortingPhotoelectricSignal;
        
        Log.Information("标定对话框已订阅光电信号事件");
    }

    #region Properties

    public string Title
    {
        [UsedImplicitly] get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>
    /// 触发时间 - 用于XAML数据绑定
    /// </summary>
    public DateTime? TriggerTime
    {
        [UsedImplicitly] get => _triggerTime;
        private set
        { 
            if (SetProperty(ref _triggerTime, value))
            {
                RaisePropertyChanged(nameof(IsTriggerTimeVisible));
            }
        }
    }

    /// <summary>
    /// 分拣时间 - 用于XAML数据绑定
    /// </summary>
    public DateTime? SortingTime
    {
        [UsedImplicitly] get => _sortingTime;
        private set => SetProperty(ref _sortingTime, value);
    }

    [UsedImplicitly]
    public bool IsTriggerTimeVisible
    {
        get => TriggerTime.HasValue;
    }

    public double MeasuredDelay
    {
        [UsedImplicitly] get => _measuredDelay;
        private set => SetProperty(ref _measuredDelay, value);
    }

    public string StatusMessage
    {
        [UsedImplicitly] get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsCalibrationMode
    {
        [UsedImplicitly] get => _isCalibrationMode;
        set
        {
            if (!SetProperty(ref _isCalibrationMode, value)) return;
            if (value)
            {
                StatusMessage = "标定模式已启用，等待触发光电信号...";
                ResetMeasurement();
            }
            else
            {
                StatusMessage = "标定模式已关闭";
            }
        }
    }

    public string SelectedPhotoelectric
    {
        [UsedImplicitly] get => _selectedPhotoelectric;
        set 
        { 
            if (SetProperty(ref _selectedPhotoelectric, value))
            {
                LoadSelectedPhotoelectricConfig();
            }
        }
    }

    public double TimeRangeLower
    {
        [UsedImplicitly] get => _timeRangeLower;
        set => SetProperty(ref _timeRangeLower, value);
    }

    public double TimeRangeUpper
    {
        [UsedImplicitly] get => _timeRangeUpper;
        set => SetProperty(ref _timeRangeUpper, value);
    }

    public double SortingDelay
    {
        [UsedImplicitly] get => _sortingDelay;
        set => SetProperty(ref _sortingDelay, value);
    }

    public double ResetDelay
    {
        [UsedImplicitly] get => _resetDelay;
        set => SetProperty(ref _resetDelay, value);
    }

    [UsedImplicitly]
    public ObservableCollection<string> AvailablePhotoelectrics { get; } = new();
    
    [UsedImplicitly]
    public ObservableCollection<CalibrationResult> CalibrationHistory { get; } = new();

    #endregion

    #region Commands

    public DelegateCommand ToggleCalibrationModeCommand { [UsedImplicitly] get; }
    public DelegateCommand ApplyRecommendedSettingsCommand { [UsedImplicitly] get; }
    public DelegateCommand SaveCommand { [UsedImplicitly] get; }
    public DelegateCommand CancelCommand { [UsedImplicitly] get; }

    #endregion

    #region Private Methods

    private void InitializePhotoelectrics()
    {
        AvailablePhotoelectrics.Clear();
        AvailablePhotoelectrics.Add("触发光电");
        
        foreach (var photoelectric in _config.SortingPhotoelectrics)
        {
            AvailablePhotoelectrics.Add($"{photoelectric.Name} (格口: {GetPhotoelectricSlots(photoelectric.Name)})");
        }
    }

    private string GetPhotoelectricSlots(string photoelectricName)
    {
        var photoelectric = _config.SortingPhotoelectrics.FirstOrDefault(p => p.Name == photoelectricName);
        if (photoelectric == null) return "1-2"; // 默认格口
        var index = _config.SortingPhotoelectrics.IndexOf(photoelectric);
        if (index < 0) return "1-2"; // 默认格口
        var startSlot = index * 2 + 1;
        var endSlot = startSlot + 1;
        return $"{startSlot}-{endSlot}";
    }

    private void LoadSelectedPhotoelectricConfig()
    {
        try
        {
            if (SelectedPhotoelectric.Contains("触发光电"))
            {
                TimeRangeLower = _config.TriggerPhotoelectric.SortingTimeRangeLower;
                TimeRangeUpper = _config.TriggerPhotoelectric.SortingTimeRangeUpper;
                SortingDelay = _config.TriggerPhotoelectric.SortingDelay;
                ResetDelay = _config.TriggerPhotoelectric.ResetDelay;
            }
            else
            {
                var photoelectricName = SelectedPhotoelectric.Split(" (")[0];
                var photoelectric = _config.SortingPhotoelectrics.FirstOrDefault(p => p.Name == photoelectricName);
                if (photoelectric == null) return;
                TimeRangeLower = photoelectric.TimeRangeLower;
                TimeRangeUpper = photoelectric.TimeRangeUpper;
                SortingDelay = photoelectric.SortingDelay;
                ResetDelay = photoelectric.ResetDelay;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载光电配置时发生错误");
        }
    }

    private void OnTriggerPhotoelectricSignal(object? sender, DateTime triggerTime)
    {
        if (!IsCalibrationMode) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            TriggerTime = triggerTime;
            SortingTime = null;
            MeasuredDelay = 0;
            StatusMessage = $"已记录触发时间: {triggerTime:HH:mm:ss.fff}，等待分拣光电信号...";
            
            Log.Information("标定模式：记录触发时间 {TriggerTime:HH:mm:ss.fff}", triggerTime);
        });
    }

    private void OnSortingPhotoelectricSignal(object? sender, (string PhotoelectricName, DateTime SignalTime) args)
    {
        if (!IsCalibrationMode || !TriggerTime.HasValue) return;

        // 检查是否是当前选中的光电
        var currentPhotoelectricName =
            // 对于触发光电模式（单光电），分拣信号来自"默认"光电
            SelectedPhotoelectric.Contains("触发光电") ? "默认" : SelectedPhotoelectric.Split(" (")[0];

        if (args.PhotoelectricName != currentPhotoelectricName) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            SortingTime = args.SignalTime;
            var delay = (args.SignalTime - TriggerTime.Value).TotalMilliseconds;
            MeasuredDelay = delay;
            
            StatusMessage = $"测量完成！时间差: {delay:F1}ms (触发: {TriggerTime:HH:mm:ss.fff} → 分拣: {args.SignalTime:HH:mm:ss.fff})";
            
            // 添加到历史记录
            var result = new CalibrationResult
            {
                Timestamp = DateTime.Now,
                PhotoelectricName = args.PhotoelectricName,
                TriggerTime = TriggerTime.Value,
                SortingTime = args.SignalTime,
                MeasuredDelay = delay
            };
            
            CalibrationHistory.Insert(0, result);
            
            // 限制历史记录数量
            while (CalibrationHistory.Count > 20)
            {
                CalibrationHistory.RemoveAt(CalibrationHistory.Count - 1);
            }
            
            ApplyRecommendedSettingsCommand.RaiseCanExecuteChanged();
            
            Log.Information("标定测量完成 - 光电: {PhotoelectricName}, 延迟: {Delay:F1}ms", args.PhotoelectricName, delay);
        });
    }

    private void ResetMeasurement()
    {
        TriggerTime = null;
        SortingTime = null;
        MeasuredDelay = 0;
    }

    private bool CanApplyRecommendedSettings()
    {
        return CalibrationHistory.Count >= 3;
    }

    private void ExecuteApplyRecommendedSettings()
    {
        try
        {
            var recentResults = CalibrationHistory.Take(5).ToList();
            var avgDelay = recentResults.Average(r => r.MeasuredDelay);
            var minDelay = recentResults.Min(r => r.MeasuredDelay);
            var maxDelay = recentResults.Max(r => r.MeasuredDelay);
            
            // 基于测量结果推荐参数
            TimeRangeLower = Math.Max(0, minDelay - 100);
            TimeRangeUpper = maxDelay + 100;
            SortingDelay = Math.Max(0, avgDelay - 50);
            
            StatusMessage = $"已应用推荐设置 (基于最近{recentResults.Count}次测量): 范围 {TimeRangeLower:F0}-{TimeRangeUpper:F0}ms, 延迟 {SortingDelay:F0}ms";
            
            Log.Information("应用推荐设置: 时间范围 {Lower:F0}-{Upper:F0}ms, 分拣延迟 {Delay:F0}ms", 
                TimeRangeLower, TimeRangeUpper, SortingDelay);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "应用推荐设置时发生错误");
            StatusMessage = "应用推荐设置失败，请检查测量数据";
        }
    }

    private void ExecuteSave()
    {
        try
        {
            if (SelectedPhotoelectric.Contains("触发光电"))
            {
                // 保存到触发光电配置
                _config.TriggerPhotoelectric.SortingTimeRangeLower = (int)TimeRangeLower;
                _config.TriggerPhotoelectric.SortingTimeRangeUpper = (int)TimeRangeUpper;
                _config.TriggerPhotoelectric.SortingDelay = (int)SortingDelay;
                _config.TriggerPhotoelectric.ResetDelay = (int)ResetDelay;
            }
            else
            {
                // 保存到分拣光电配置
                var photoelectricName = SelectedPhotoelectric.Split(" (")[0];
                var photoelectric = _config.SortingPhotoelectrics.FirstOrDefault(p => p.Name == photoelectricName);
                if (photoelectric != null)
                {
                    photoelectric.TimeRangeLower = (int)TimeRangeLower;
                    photoelectric.TimeRangeUpper = (int)TimeRangeUpper;
                    photoelectric.SortingDelay = (int)SortingDelay;
                    photoelectric.ResetDelay = (int)ResetDelay;
                }
            }
            
            _settingsService.SaveSettings(_config);
            
            Log.Information("标定配置已保存: 光电 {PhotoelectricName}, 时间范围 {Lower:F0}-{Upper:F0}ms, 分拣延迟 {SortingDelay:F0}ms, 回正延迟 {ResetDelay:F0}ms", 
                SelectedPhotoelectric, TimeRangeLower, TimeRangeUpper, SortingDelay, ResetDelay);
            
            StatusMessage = "配置已保存并生效";
            
            // 自动关闭对话框
            RequestClose.Invoke(new DialogResult(ButtonResult.OK));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存标定配置时发生错误");
            StatusMessage = "保存配置失败，请重试";
        }
    }

    private void ExecuteCancel()
    {
        RequestClose.Invoke(new DialogResult(ButtonResult.Cancel));
    }

    #endregion

    #region IDialogAware

    public DialogCloseListener RequestClose { get; private set; } = new DialogCloseListener();

    public bool CanCloseDialog() => true;

    public void OnDialogClosed()
    {
        // 退出标定模式
        IsCalibrationMode = false;
        
        // 取消订阅事件
        _sortService.TriggerPhotoelectricSignal -= OnTriggerPhotoelectricSignal;
        _sortService.SortingPhotoelectricSignal -= OnSortingPhotoelectricSignal;
        
        Log.Information("标定对话框已关闭并取消订阅光电信号事件");
    }

    public void OnDialogOpened(IDialogParameters parameters)
    {
        // 对话框打开时的初始化
        StatusMessage = "请先启用标定模式，然后等待包裹通过触发分拣时间标定";
    }

    #endregion
}

public class CalibrationResult
{
    [UsedImplicitly]
    public DateTime Timestamp { get; set; }
    
    [UsedImplicitly]
    public string PhotoelectricName { get; set; } = string.Empty;
    
    [UsedImplicitly]
    public DateTime TriggerTime { get; set; }
    
    [UsedImplicitly]
    public DateTime SortingTime { get; set; }
    
    [UsedImplicitly]
    public double MeasuredDelay { get; set; }
}
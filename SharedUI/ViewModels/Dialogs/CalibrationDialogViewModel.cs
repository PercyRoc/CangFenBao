using System.Collections.ObjectModel;
using System.Windows;
using JetBrains.Annotations;
using Serilog;
using Common.Events;
using SharedUI.Models;

namespace SharedUI.ViewModels.Dialogs;

public class CalibrationDialogViewModel : BindableBase, IDialogAware
{
    private readonly IEventAggregator _eventAggregator;
    private bool _isCalibrationMode;
    private double _measuredDelay;
    private double _resetDelay = 1000;
    private CalibrationTarget? _selectedTarget;
    private double _sortingDelay = 50;
    private DateTime? _secondSignalTime;
    private string _statusMessage = "请启用一次性标定模式，然后让包裹通过触发标定";
    private double _timeRangeLower = 1000;
    private double _timeRangeUpper = 3000;
    private string _title = "一次性标定";
    private DateTime? _triggerTime;
    private SubscriptionToken? _triggerToken;
    private SubscriptionToken? _sortingToken;
    private SubscriptionToken? _packageProcessingToken;

    // 完整标定流程的状态
    private DateTime? _packageProcessingTime;
    private bool _hasTriggerTimeResult;
    private bool _hasSortingTimeResult;

    public CalibrationDialogViewModel(IEventAggregator eventAggregator)
    {
        _eventAggregator = eventAggregator;

        ToggleCalibrationModeCommand = new DelegateCommand(() => IsCalibrationMode = !IsCalibrationMode);
        ApplyRecommendedSettingsCommand = new DelegateCommand(ExecuteApplyRecommendedSettings, CanApplyRecommendedSettings);
        SaveCommand = new DelegateCommand(ExecuteSave);
        CancelCommand = new DelegateCommand(ExecuteCancel);
    }

    #region Properties

    public string Title
    {
        [UsedImplicitly] get => _title;
        set => SetProperty(ref _title, value);
    }

    public DateTime? TriggerTime
    {
        [UsedImplicitly] get => _triggerTime;
        private set
        {
            if (SetProperty(ref _triggerTime, value))
            {
                RaisePropertyChanged(nameof(IsResultVisible));
            }
        }
    }

    public DateTime? SecondSignalTime
    {
        [UsedImplicitly] get => _secondSignalTime;
        private set => SetProperty(ref _secondSignalTime, value);
    }

    [UsedImplicitly]
    public bool IsResultVisible => TriggerTime.HasValue;

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
                StatusMessage = "一次性标定模式已启用，等待触发光电信号...";
                ResetMeasurement();
            }
            else
            {
                StatusMessage = "一次性标定模式已关闭";
            }
        }
    }

    public CalibrationTarget? SelectedTarget
    {
        [UsedImplicitly] get => _selectedTarget;
        set
        {
            if (SetProperty(ref _selectedTarget, value))
            {
                LoadSelectedTargetConfig();
                SubscribeToEvents(); // Re-subscribe based on new target mode
                RaisePropertyChanged(nameof(SecondSignalName));
                RaisePropertyChanged(nameof(IsSortingDelayVisible));
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
    public ObservableCollection<CalibrationTarget> AvailableTargets { get; } = new();

    [UsedImplicitly]
    public ObservableCollection<CalibrationResult> CalibrationHistory { get; } = new();

    /// <summary>
    /// Gets the name for the second signal based on the current calibration mode.
    /// </summary>
    public string SecondSignalName => SelectedTarget?.Mode == CalibrationMode.TriggerTime ? "处理时间" : "分拣时间";

    /// <summary>
    /// Determines if the SortingDelay and ResetDelay controls should be visible.
    /// </summary>
    public bool IsSortingDelayVisible => SelectedTarget?.Mode == CalibrationMode.SortingTime || SelectedTarget?.Mode == CalibrationMode.CompleteFlow;

    #endregion

    #region Commands

    public DelegateCommand ToggleCalibrationModeCommand { [UsedImplicitly] get; }
    public DelegateCommand ApplyRecommendedSettingsCommand { [UsedImplicitly] get; }
    public DelegateCommand SaveCommand { [UsedImplicitly] get; }
    public DelegateCommand CancelCommand { [UsedImplicitly] get; }

    #endregion

    #region Private Methods

    private void LoadSelectedTargetConfig()
    {
        if (SelectedTarget == null) return;

        TimeRangeLower = SelectedTarget.TimeRangeLower;
        TimeRangeUpper = SelectedTarget.TimeRangeUpper;
        SortingDelay = SelectedTarget.SortingDelay;
        ResetDelay = SelectedTarget.ResetDelay;
    }

    private void OnTriggerSignal(DateTime triggerTime)
    {
        Log.Debug("收到触发信号事件: {TriggerTime:HH:mm:ss.fff}, 标定模式: {IsCalibrationMode}", triggerTime, IsCalibrationMode);
        
        if (!IsCalibrationMode) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            TriggerTime = triggerTime;
            SecondSignalTime = null;
            _packageProcessingTime = null;
            MeasuredDelay = 0;
            _hasTriggerTimeResult = false;
            _hasSortingTimeResult = false;
            
            if (SelectedTarget?.Mode == CalibrationMode.CompleteFlow)
            {
                StatusMessage = $"已记录触发时间: {triggerTime:HH:mm:ss.fff}，等待包裹处理和分拣信号...";
            }
            else
            {
                StatusMessage = $"已记录触发时间: {triggerTime:HH:mm:ss.fff}，等待{SecondSignalName}信号...";
            }
            
            Log.Information("一次性标定模式：记录触发时间 {TriggerTime:HH:mm:ss.fff}", triggerTime);
        });
    }

    private void OnSortingSignal((string PhotoelectricName, DateTime SignalTime) args)
    {
        Log.Debug("收到分拣信号事件: 光电={PhotoelectricName}, 时间={SignalTime:HH:mm:ss.fff}, 标定模式={IsCalibrationMode}, 目标模式={TargetMode}", 
            args.PhotoelectricName, args.SignalTime, IsCalibrationMode, SelectedTarget?.Mode);
        
        if (SelectedTarget?.Mode == CalibrationMode.SortingTime || SelectedTarget?.Mode == CalibrationMode.CompleteFlow)
        {
            ProcessSortingSignal(args.PhotoelectricName, args.SignalTime);
        }
        else
        {
            Log.Debug("分拣信号被忽略，当前模式不匹配");
        }
    }
    
    private void OnPackageProcessing(DateTime processingTime)
    {
        Log.Debug("收到包裹处理事件: {ProcessingTime:HH:mm:ss.fff}, 标定模式={IsCalibrationMode}, 目标模式={TargetMode}", 
            processingTime, IsCalibrationMode, SelectedTarget?.Mode);
        
        if (SelectedTarget?.Mode == CalibrationMode.TriggerTime || SelectedTarget?.Mode == CalibrationMode.CompleteFlow)
        {
            ProcessPackageProcessing(processingTime);
        }
        else
        {
            Log.Debug("包裹处理事件被忽略，当前模式不匹配");
        }
    }

    private void ProcessSortingSignal(string signalId, DateTime signalTime)
    {
        Log.Debug("处理分拣信号: 光电ID={SignalId}, 时间={SignalTime:HH:mm:ss.fff}, 标定模式={IsCalibrationMode}, 触发时间={TriggerTime}, 目标ID={TargetId}", 
            signalId, signalTime, IsCalibrationMode, TriggerTime, SelectedTarget?.Id);
        
        if (!IsCalibrationMode || !TriggerTime.HasValue || SelectedTarget == null) 
        {
            Log.Debug("分拣信号处理被跳过：标定模式={IsCalibrationMode}, 触发时间={HasTriggerTime}, 目标={HasTarget}", 
                IsCalibrationMode, TriggerTime.HasValue, SelectedTarget != null);
            return;
        }

        // 对于完整标定流程，接受任何分拣光电的信号
        // 对于其他模式，需要匹配特定的光电ID
        if (SelectedTarget.Mode != CalibrationMode.CompleteFlow && signalId != SelectedTarget.Id) 
        {
            Log.Debug("分拣信号ID不匹配：收到={SignalId}, 期望={TargetId}", signalId, SelectedTarget.Id);
            return;
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            SecondSignalTime = signalTime;
            var delay = (signalTime - TriggerTime.Value).TotalMilliseconds;
            MeasuredDelay = delay;

            if (SelectedTarget.Mode == CalibrationMode.CompleteFlow)
            {
                _hasSortingTimeResult = true;
                StatusMessage = $"分拣时间测量完成！时间差: {delay:F1}ms (触发: {TriggerTime:HH:mm:ss.fff} → 分拣: {signalTime:HH:mm:ss.fff})";
                
                // 如果已经有触发时间结果，完成完整标定
                if (_hasTriggerTimeResult)
                {
                    CompleteFullCalibration();
                }
            }
            else
            {
                StatusMessage = $"测量完成！时间差: {delay:F1}ms (触发: {TriggerTime:HH:mm:ss.fff} → {SecondSignalName}: {signalTime:HH:mm:ss.fff})";
                CompleteCalibration(delay, signalTime);
            }

            Log.Information("分拣时间标定测量完成 - 标定项: {Target}, 延迟: {Delay:F1}ms", SelectedTarget.DisplayName, delay);
        });
    }

    private void ProcessPackageProcessing(DateTime processingTime)
    {
        Log.Debug("处理包裹处理信号: 时间={ProcessingTime:HH:mm:ss.fff}, 标定模式={IsCalibrationMode}, 触发时间={TriggerTime}, 目标={HasTarget}", 
            processingTime, IsCalibrationMode, TriggerTime, SelectedTarget != null);
        
        if (!IsCalibrationMode || !TriggerTime.HasValue || SelectedTarget == null) 
        {
            Log.Debug("包裹处理信号被跳过：标定模式={IsCalibrationMode}, 触发时间={HasTriggerTime}, 目标={HasTarget}", 
                IsCalibrationMode, TriggerTime.HasValue, SelectedTarget != null);
            return;
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            _packageProcessingTime = processingTime;
            var delay = (processingTime - TriggerTime.Value).TotalMilliseconds;

            if (SelectedTarget.Mode == CalibrationMode.CompleteFlow)
            {
                _hasTriggerTimeResult = true;
                StatusMessage = $"触发时间测量完成！时间差: {delay:F1}ms (触发: {TriggerTime:HH:mm:ss.fff} → 处理: {processingTime:HH:mm:ss.fff})";
                
                // 如果已经有分拣时间结果，完成完整标定
                if (_hasSortingTimeResult)
                {
                    CompleteFullCalibration();
                }
            }
            else
            {
                StatusMessage = $"测量完成！时间差: {delay:F1}ms (触发: {TriggerTime:HH:mm:ss.fff} → 处理: {processingTime:HH:mm:ss.fff})";
                CompleteCalibration(delay, processingTime);
            }

            Log.Information("触发时间标定测量完成 - 标定项: {Target}, 延迟: {Delay:F1}ms", SelectedTarget.DisplayName, delay);
        });
    }

    private void CompleteFullCalibration()
    {
        if (!_packageProcessingTime.HasValue || !SecondSignalTime.HasValue) return;

        var triggerTimeDelay = (_packageProcessingTime.Value - TriggerTime.Value).TotalMilliseconds;
        var sortingTimeDelay = (SecondSignalTime.Value - TriggerTime.Value).TotalMilliseconds;

        var result = new CalibrationResult
        {
            Timestamp = DateTime.Now,
            PhotoelectricName = SelectedTarget?.DisplayName ?? "",
            TriggerTime = TriggerTime.Value,
            SortingTime = SecondSignalTime.Value,
            MeasuredDelay = sortingTimeDelay, // 主要显示分拣时间延迟
            TriggerTimeDelay = triggerTimeDelay,
            SortingTimeDelay = sortingTimeDelay,
            Mode = CalibrationMode.CompleteFlow
        };

        CalibrationHistory.Insert(0, result);
        
        while (CalibrationHistory.Count > 20)
        {
            CalibrationHistory.RemoveAt(CalibrationHistory.Count - 1);
        }

        ApplyRecommendedSettingsCommand.RaiseCanExecuteChanged();
        
        StatusMessage = $"完整标定完成！触发时间: {triggerTimeDelay:F1}ms, 分拣时间: {sortingTimeDelay:F1}ms";
        Log.Information("完整标定完成 - 标定项: {Target}, 触发延迟: {TriggerDelay:F1}ms, 分拣延迟: {SortingDelay:F1}ms", 
            SelectedTarget?.DisplayName, triggerTimeDelay, sortingTimeDelay);

        // 一次性标定：测量完成后自动关闭标定模式
        IsCalibrationMode = false;
        StatusMessage = $"一次性完整标定完成！触发时间: {triggerTimeDelay:F1}ms, 分拣时间: {sortingTimeDelay:F1}ms，标定模式已自动关闭";
        Log.Information("一次性完整标定完成，已自动关闭标定模式");
    }

    private void CompleteCalibration(double delay, DateTime signalTime)
    {
        var result = new CalibrationResult
        {
            Timestamp = DateTime.Now,
            PhotoelectricName = SelectedTarget?.DisplayName ?? "",
            TriggerTime = TriggerTime.Value,
            SortingTime = signalTime,
            MeasuredDelay = delay,
            Mode = SelectedTarget?.Mode ?? CalibrationMode.SortingTime
        };

        CalibrationHistory.Insert(0, result);
        
        while (CalibrationHistory.Count > 20)
        {
            CalibrationHistory.RemoveAt(CalibrationHistory.Count - 1);
        }

        ApplyRecommendedSettingsCommand.RaiseCanExecuteChanged();

        // 一次性标定：测量完成后自动关闭标定模式
        IsCalibrationMode = false;
        StatusMessage = $"一次性标定完成！时间差: {delay:F1}ms，标定模式已自动关闭";
        Log.Information("一次性标定完成，已自动关闭标定模式");
    }

    private void ResetMeasurement()
    {
        TriggerTime = null;
        SecondSignalTime = null;
        _packageProcessingTime = null;
        MeasuredDelay = 0;
        _hasTriggerTimeResult = false;
        _hasSortingTimeResult = false;
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
            if (!recentResults.Any())
            {
                StatusMessage = "无足够的测量数据以应用推荐设置";
                return;
            }
            
            var avgDelay = recentResults.Average(r => r.MeasuredDelay);
            var minDelay = recentResults.Min(r => r.MeasuredDelay);
            var maxDelay = recentResults.Max(r => r.MeasuredDelay);

            TimeRangeLower = Math.Max(0, minDelay - 100);
            TimeRangeUpper = maxDelay + 100;
            SortingDelay = Math.Max(0, avgDelay - 50);

            StatusMessage = $"已应用推荐设置 (基于最近{recentResults.Count}次测量): 范围 {TimeRangeLower:F0}-{TimeRangeUpper:F0}ms, 延迟 {SortingDelay:F0}ms";
            Log.Information("应用推荐设置 (模式: {Mode}): 时间范围 {Lower:F0}-{Upper:F0}ms, 分拣延迟 {Delay:F0}ms", 
                SelectedTarget?.Mode, TimeRangeLower, TimeRangeUpper, SortingDelay);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "应用推荐设置时发生错误");
            StatusMessage = "应用推荐设置失败，请检查测量数据";
        }
    }

    private void ExecuteSave()
    {
        if (SelectedTarget == null) return;
        
        SelectedTarget.TimeRangeLower = TimeRangeLower;
        SelectedTarget.TimeRangeUpper = TimeRangeUpper;
        SelectedTarget.SortingDelay = SortingDelay;
        SelectedTarget.ResetDelay = ResetDelay;
        
        Log.Information("标定配置已更新: 光电 {PhotoelectricName}, 时间范围 {Lower:F0}-{Upper:F0}ms, 分拣延迟 {SortingDelay:F0}ms, 回正延迟 {ResetDelay:F0}ms",
            SelectedTarget.DisplayName, TimeRangeLower, TimeRangeUpper, SortingDelay, ResetDelay);

        StatusMessage = "配置已在本地更新，请在主设置页面保存以持久化";

        RequestClose.Invoke(new DialogResult(ButtonResult.OK) { Parameters = new DialogParameters { { "targets", AvailableTargets.ToList() } } });
    }

    private void ExecuteCancel()
    {
        RequestClose.Invoke(new DialogResult(ButtonResult.Cancel));
    }

    private void SubscribeToEvents()
    {
        UnsubscribeFromEvents();
        
        _triggerToken = _eventAggregator.GetEvent<TriggerSignalEvent>().Subscribe(OnTriggerSignal, ThreadOption.UIThread);

        if (SelectedTarget?.Mode == CalibrationMode.SortingTime)
        {
            _sortingToken = _eventAggregator.GetEvent<SortingSignalEvent>().Subscribe(OnSortingSignal, ThreadOption.UIThread);
        }
        else if (SelectedTarget?.Mode == CalibrationMode.TriggerTime)
        {
            _packageProcessingToken = _eventAggregator.GetEvent<PackageProcessingEvent>().Subscribe(OnPackageProcessing, ThreadOption.UIThread);
        }
        else if (SelectedTarget?.Mode == CalibrationMode.CompleteFlow)
        {
            // 完整流程需要同时订阅两种信号
            _sortingToken = _eventAggregator.GetEvent<SortingSignalEvent>().Subscribe(OnSortingSignal, ThreadOption.UIThread);
            _packageProcessingToken = _eventAggregator.GetEvent<PackageProcessingEvent>().Subscribe(OnPackageProcessing, ThreadOption.UIThread);
        }
    }

    private void UnsubscribeFromEvents()
    {
        _eventAggregator.GetEvent<TriggerSignalEvent>().Unsubscribe(_triggerToken);
        _eventAggregator.GetEvent<SortingSignalEvent>().Unsubscribe(_sortingToken);
        _eventAggregator.GetEvent<PackageProcessingEvent>().Unsubscribe(_packageProcessingToken);
    }

    #endregion

    #region IDialogAware

    public DialogCloseListener RequestClose { get; } = new();

    public bool CanCloseDialog() => true;

    public void OnDialogClosed()
    {
        IsCalibrationMode = false;
        UnsubscribeFromEvents();
        Log.Information("一次性标定对话框已关闭并取消订阅光电信号事件");
    }

    public void OnDialogOpened(IDialogParameters parameters)
    {
        var targets = parameters.GetValue<IEnumerable<CalibrationTarget>>("targets");
        if (targets != null)
        {
            AvailableTargets.Clear();
            AvailableTargets.AddRange(targets);
            SelectedTarget = AvailableTargets.FirstOrDefault();
        }
        
        StatusMessage = "请启用一次性标定模式，然后让包裹通过触发完整标定流程";
    }

    #endregion
} 
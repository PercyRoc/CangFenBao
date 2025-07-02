using System.Collections.ObjectModel;
using Common.Services.Ui;
using Serilog;
using ShanghaiModuleBelt.Models;
using ShanghaiModuleBelt.Services;

namespace ShanghaiModuleBelt.ViewModels;

/// <summary>
///     格口统计对话框视图模型
/// </summary>
internal class ChuteStatisticsDialogViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly RetryService _retryService;

    public ChuteStatisticsDialogViewModel(INotificationService notificationService, RetryService retryService)
    {
        _notificationService = notificationService;
        _retryService = retryService;
        RefreshCommand = new DelegateCommand(ExecuteRefresh);
        RetryFailedDataCommand = new DelegateCommand(ExecuteRetryFailedData);
    }
    /// <summary>
    ///     格口统计集合
    /// </summary>
    public ObservableCollection<ChuteStatistics> ChuteStatistics { get; } = [];

    /// <summary>
    ///     刷新命令
    /// </summary>
    public DelegateCommand RefreshCommand { get; set; }

    /// <summary>
    ///     刷新数据的委托
    /// </summary>
    public Action? RefreshAction { get; set; }

    public DelegateCommand RetryFailedDataCommand { get; }

    /// <summary>
    ///     更新格口统计数据
    /// </summary>
    /// <param name="chutePackageCount">格口包裹计数字典</param>
    public void UpdateStatistics(Dictionary<int, int> chutePackageCount)
    {
        ChuteStatistics.Clear();

        // 按格口号排序并创建统计对象
        foreach (var kvp in chutePackageCount.OrderBy(x => x.Key))
        {
            var statistics = new ChuteStatistics
            {
                ChuteNumber = kvp.Key,
                PackageCount = kvp.Value
            };

            ChuteStatistics.Add(statistics);
        }
    }

    private void ExecuteRefresh()
    {
        // 执行外部提供的刷新动作
        RefreshAction?.Invoke();
    }

    private async void ExecuteRetryFailedData()
    {
        try
        {
            // 调用 RetryService 的重传方法
            await _retryService.PerformRetryAsync();
            _notificationService.ShowSuccess("重传成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "手动重传失败数据时发生错误");
            _notificationService.ShowError($"重传失败数据时发生错误：{ex.Message}");
        }
        finally
        {
            RefreshAction?.Invoke(); // 重传完成后刷新统计数据
        }
    }
}
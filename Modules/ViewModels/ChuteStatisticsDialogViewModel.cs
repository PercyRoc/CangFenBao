using System.Collections.ObjectModel;
using ShanghaiModuleBelt.Models;

namespace ShanghaiModuleBelt.ViewModels;

/// <summary>
///     格口统计对话框视图模型
/// </summary>
internal class ChuteStatisticsDialogViewModel : BindableBase
{
    public ChuteStatisticsDialogViewModel()
    {
        RefreshCommand = new DelegateCommand(ExecuteRefresh);
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

    // 重传功能已移除

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

    // 重传功能已移除
}
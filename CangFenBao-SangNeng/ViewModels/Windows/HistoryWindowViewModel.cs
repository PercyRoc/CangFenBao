using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.IO;
using CangFenBao_SangNeng.Models;
using CommonLibrary.Models;
using CommonLibrary.Services;
using Presentation_CommonLibrary.Services;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Services.Dialogs;
using static CommonLibrary.Models.PackageStatus;

namespace CangFenBao_SangNeng.ViewModels.Windows;

/// <summary>
/// 历史记录查询窗口视图模型
/// </summary>
public class HistoryWindowViewModel : BindableBase, IDialogAware
{
    private readonly IPackageDataService _packageDataService;
    private readonly INotificationService _notificationService;
    private DateTime _startDate = DateTime.Today;
    private DateTime _endDate = DateTime.Today;
    private StatusOption? _selectedStatus;
    private string _searchBarcode = string.Empty;
    private ObservableCollection<PackageRecord> _packageRecords = [];

    /// <summary>
    /// 开始日期
    /// </summary>
    public DateTime StartDate
    {
        get => _startDate;
        set => SetProperty(ref _startDate, value);
    }

    /// <summary>
    /// 结束日期
    /// </summary>
    public DateTime EndDate
    {
        get => _endDate;
        set => SetProperty(ref _endDate, value);
    }

    /// <summary>
    /// 状态列表
    /// </summary>
    public ObservableCollection<StatusOption> StatusList { get; } =
    [
        new(null, "All"),
        new(Created, "Created"),
        new(Measuring, "Measuring"),
        new(MeasureSuccess, "Success"),
        new(MeasureFailed, "Failed")
    ];

    /// <summary>
    /// 选中的状态
    /// </summary>
    public StatusOption? SelectedStatus
    {
        get => _selectedStatus;
        set => SetProperty(ref _selectedStatus, value);
    }

    /// <summary>
    /// 搜索条码
    /// </summary>
    public string SearchBarcode
    {
        get => _searchBarcode;
        set => SetProperty(ref _searchBarcode, value);
    }

    /// <summary>
    /// 包裹记录列表
    /// </summary>
    public ObservableCollection<PackageRecord> PackageRecords
    {
        get => _packageRecords;
        private set => SetProperty(ref _packageRecords, value);
    }

    /// <summary>
    /// 查询命令
    /// </summary>
    public ICommand QueryCommand { get; }

    /// <summary>
    /// 查看图片命令
    /// </summary>
    public ICommand ViewImageCommand { get; }

    /// <summary>
    /// 构造函数
    /// </summary>
    public HistoryWindowViewModel(
        IPackageDataService packageDataService,
        INotificationService notificationService)
    {
        _packageDataService = packageDataService;
        _notificationService = notificationService;
        SelectedStatus = StatusList[0]; // 默认选择"All"
        QueryCommand = new DelegateCommand(QueryAsync);
        ViewImageCommand = new DelegateCommand<PackageRecord>(ViewImage);
    }

    /// <summary>
    /// 执行查询
    /// </summary>
    private async void QueryAsync()
    {
        var startTime = StartDate.Date;
        var endTime = EndDate.Date.AddDays(1).AddSeconds(-1);

        // 获取基础查询结果
        List<PackageRecord> records;
        if (SelectedStatus?.Status == null)
        {
            // 查询所有状态
            records = await _packageDataService.GetPackagesInTimeRangeAsync(startTime, endTime);
        }
        else
        {
            // 查询指定状态
            records = await _packageDataService.GetPackagesByStatusAsync(SelectedStatus.Status.Value, EndDate);
        }

        // 如果有条码搜索条件，进行过滤
        if (!string.IsNullOrWhiteSpace(SearchBarcode))
        {
            records = records.Where(r => r.Barcode.Contains(SearchBarcode, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        PackageRecords = new ObservableCollection<PackageRecord>(records);
    }

    private void ViewImage(PackageRecord? record)
    {
        if (record?.ImagePath == null || !File.Exists(record.ImagePath)) return;
        Process.Start(new ProcessStartInfo(record.ImagePath) { UseShellExecute = true });
    }

    public string Title => "Package History";
    
    public event Action<IDialogResult>? RequestClose;

    public bool CanCloseDialog() => true;

    public void OnDialogClosed()
    {
    }

    public void OnDialogOpened(IDialogParameters parameters)
    {
        // 执行初始查询
        QueryAsync();
    }
} 
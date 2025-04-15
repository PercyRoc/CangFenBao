using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Input;
using Common.Data;
using Common.Models.Package;
using Common.Services.Ui;
using Microsoft.Win32;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Services.Dialogs;
using Serilog;

namespace SharedUI.ViewModels;

/// <summary>
///     历史记录查询窗口视图模型
/// </summary>
public class HistoryDialogViewModel : BindableBase, IDialogAware
{
    private readonly INotificationService _notificationService;
    private readonly IPackageDataService _packageDataService;
    private DateTime _endDate = DateTime.Today;
    private bool _isLoading;
    private ObservableCollection<PackageRecord> _packageRecords = [];
    private string _searchBarcode = string.Empty;
    private string _searchChute = string.Empty;
    private DateTime _startDate = DateTime.Today;
    private string _selectedStatus = "全部";

    /// <summary>
    ///     状态列表 - 动态生成
    /// </summary>
    public List<string> StatusList { get; }

    /// <summary>
    ///     选中的状态
    /// </summary>
    public string SelectedStatus
    {
        get => _selectedStatus;
        set
        {
            if (SetProperty(ref _selectedStatus, value))
            {
                QueryAsync();
            }
        }
    }

    /// <summary>
    ///     构造函数
    /// </summary>
    public HistoryDialogViewModel(
        IPackageDataService packageDataService,
        INotificationService notificationService)
    {
        _packageDataService = packageDataService;
        _notificationService = notificationService;

        // 动态生成状态列表
        var statusOptions = new List<string> { "全部" }; // 添加 "全部" 选项
        var displayNames = Enum.GetValues<PackageStatus>()
                               .Select(GetStatusDisplay) // 获取所有状态的显示名称
                               .Distinct() // 去除重复的显示名称
                               .OrderBy(s => s); // 按名称排序
        statusOptions.AddRange(displayNames);
        StatusList = statusOptions;
        // _selectedStatus 默认是 "全部", 无需重新设置

        QueryCommand = new DelegateCommand(QueryAsync);
        ExportToExcelCommand = new DelegateCommand(ExecuteExportToExcel, CanExportToExcel)
            .ObservesProperty(() => PackageRecords.Count);
        OpenImageCommand = new DelegateCommand<string>(OpenImageExecute, CanOpenImage);
    }

    /// <summary>
    ///     开始日期
    /// </summary>
    public DateTime StartDate
    {
        get => _startDate;
        set => SetProperty(ref _startDate, value);
    }

    /// <summary>
    ///     结束日期
    /// </summary>
    public DateTime EndDate
    {
        get => _endDate;
        set => SetProperty(ref _endDate, value);
    }

    /// <summary>
    ///     搜索条码
    /// </summary>
    public string SearchBarcode
    {
        get => _searchBarcode;
        set => SetProperty(ref _searchBarcode, value);
    }

    /// <summary>
    ///     搜索格口
    /// </summary>
    public string SearchChute
    {
        get => _searchChute;
        set => SetProperty(ref _searchChute, value);
    }

    /// <summary>
    ///     包裹记录列表
    /// </summary>
    public ObservableCollection<PackageRecord> PackageRecords
    {
        get => _packageRecords;
        private set => SetProperty(ref _packageRecords, value);
    }

    /// <summary>
    ///     查询命令
    /// </summary>
    public ICommand QueryCommand { get; }

    /// <summary>
    ///     导出Excel命令
    /// </summary>
    public DelegateCommand ExportToExcelCommand { get; }

    /// <summary>
    ///     是否正在加载
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    /// <summary>
    ///     打开图片命令
    /// </summary>
    public ICommand OpenImageCommand { get; }

    public string Title => "包裹历史记录";

    public event Action<IDialogResult>? RequestClose;

    public bool CanCloseDialog()
    {
        return true;
    }

    public void OnDialogClosed()
    {
    }

    public void OnDialogOpened(IDialogParameters parameters)
    {
        // 执行初始查询
        QueryAsync();
    }

    /// <summary>
    ///     执行查询
    /// </summary>
    private async void QueryAsync()
    {
        try
        {
            var startTime = StartDate.Date;
            var endTime = EndDate.Date.AddDays(1).AddSeconds(-1);

            // 打印查询参数用于调试
            Log.Information("历史记录查询参数 - 开始日期: {StartDate}, 结束日期: {EndDate}, 条码: {Barcode}, 格口: {Chute}, 状态: {Status}",
                startTime, endTime, SearchBarcode, SearchChute, SelectedStatus);

            // 显示加载状态
            IsLoading = true;

            // 获取基础查询结果
            var records = await _packageDataService.GetPackagesInTimeRangeAsync(startTime, endTime);

            // 如果有条码搜索条件，进行过滤
            if (!string.IsNullOrWhiteSpace(SearchBarcode))
            {
                records = records.Where(r => r.Barcode.Contains(SearchBarcode, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // 如果有格口搜索条件，进行过滤
            if (!string.IsNullOrWhiteSpace(SearchChute) && int.TryParse(SearchChute, out var chuteNumber))
            {
                records = records.Where(r => r.ChuteNumber == chuteNumber).ToList();
            }

            // 根据状态进行过滤
            if (SelectedStatus != "全部")
            {
                // 查找与所选显示名称匹配的状态枚举值
                var targetStatuses = Enum.GetValues<PackageStatus>()
                                         .Where(s => GetStatusDisplay(s) == SelectedStatus)
                                         .ToList();

                if (targetStatuses.Count != 0)
                {
                    // 过滤记录，使其状态必须是目标状态之一
                    records = records.Where(r => targetStatuses.Contains(r.Status)).ToList();
                }
                else
                {
                    // 如果选中的状态字符串没有匹配到任何枚举值（理论上不应发生）
                    Log.Warning("无法将选择的状态 '{SelectedStatus}' 映射到任何 PackageStatus。", SelectedStatus);
                    records = []; // 清空结果
                }
            }

            PackageRecords = [.. records];
            _notificationService.ShowSuccessWithToken("查询成功", "HistoryWindowGrowl");
        }
        catch (Exception ex)
        {
            // 记录错误
            Log.Error(ex, "查询历史记录时发生错误");

            // 通知用户
            _notificationService.ShowErrorWithToken("查询失败", "HistoryWindowGrowl");

            // 确保有一个空的结果集
            PackageRecords = [];
        }
        finally
        {
            // 无论成功失败，都结束加载状态
            IsLoading = false;
        }
    }

    /// <summary>
    ///     判断是否可以导出Excel
    /// </summary>
    private bool CanExportToExcel()
    {
        return PackageRecords.Count > 0;
    }

    /// <summary>
    ///     导出Excel
    /// </summary>
    private void ExecuteExportToExcel()
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Excel文件|*.xlsx",
                Title = "导出包裹记录",
                FileName = $"包裹记录_{DateTime.Now:yyyyMMddHHmmss}.xlsx"
            };

            if (dialog.ShowDialog() != true) return;

            // 设置EPPlus许可证
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            using var package = new ExcelPackage();
            var worksheet = package.Workbook.Worksheets.Add("包裹记录");

            // 设置表头
            var headers = new[]
            {
                "编号", "条码", "格口", "重量(kg)", "长度(cm)", "宽度(cm)", "高度(cm)",
                "体积(cm³)", "状态", "备注", "创建时间"
            };

            for (var i = 0; i < headers.Length; i++)
            {
                worksheet.Cells[1, i + 1].Value = headers[i];
            }

            // 设置表头样式
            using (var range = worksheet.Cells[1, 1, 1, headers.Length])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(Color.LightGray);
                range.Style.Border.BorderAround(ExcelBorderStyle.Thin);
                range.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
            }

            // 写入数据
            for (var i = 0; i < PackageRecords.Count; i++)
            {
                var record = PackageRecords[i];
                var row = i + 2;

                worksheet.Cells[row, 1].Value = record.Id;
                worksheet.Cells[row, 2].Value = record.Barcode;
                worksheet.Cells[row, 3].Value = record.ChuteNumber;
                worksheet.Cells[row, 4].Value = record.Weight;
                worksheet.Cells[row, 5].Value = record.Length.HasValue ? Math.Round(record.Length.Value, 1) : null;
                worksheet.Cells[row, 6].Value = record.Width.HasValue ? Math.Round(record.Width.Value, 1) : null;
                worksheet.Cells[row, 7].Value = record.Height.HasValue ? Math.Round(record.Height.Value, 1) : null;
                worksheet.Cells[row, 8].Value = record.Volume;
                worksheet.Cells[row, 9].Value = record.StatusDisplay;
                worksheet.Cells[row, 10].Value = record.ErrorMessage;
                worksheet.Cells[row, 11].Value = record.CreateTime;

                // 设置日期格式
                worksheet.Cells[row, 11].Style.Numberformat.Format = "yyyy-MM-dd HH:mm:ss";
            }

            // 自动调整列宽
            worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();

            // 保存文件
            package.SaveAs(new FileInfo(dialog.FileName));

            _notificationService.ShowSuccessWithToken($"已成功导出 {PackageRecords.Count} 条记录", "HistoryWindowGrowl");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出Excel失败");
            _notificationService.ShowErrorWithToken($"导出失败: {ex.Message}", "HistoryWindowGrowl");
        }
    }

    /// <summary>
    ///     判断是否可以打开图片
    /// </summary>
    private bool CanOpenImage(string? imagePath)
    {
        return !string.IsNullOrEmpty(imagePath);
    }

    /// <summary>
    ///     执行打开图片的操作
    /// </summary>
    private void OpenImageExecute(string? imagePath)
    {
        if (!CanOpenImage(imagePath))
        {
            Log.Warning("Attempted to open image with invalid path: {ImagePath}", imagePath);
            return;
        }

        try
        {
            if (File.Exists(imagePath)) // Check if file exists
            {
                var processStartInfo = new ProcessStartInfo(imagePath) // Use '!' as path is checked by CanOpenImage
                {
                    UseShellExecute = true // Required to use system default app
                };
                Process.Start(processStartInfo);
                Log.Information("Opened image file: {ImagePath}", imagePath);
            }
            else
            {
                Log.Error("Image file not found at path: {ImagePath}", imagePath);
                _notificationService.ShowErrorWithToken($"图片文件未找到: {imagePath}", "HistoryWindowGrowl");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open image file: {ImagePath}", imagePath);
            _notificationService.ShowErrorWithToken($"无法打开图片: {ex.Message}", "HistoryWindowGrowl");
        }
    }

    /// <summary>
    ///     获取状态的中文显示
    /// </summary>
    private static string GetStatusDisplay(PackageStatus status)
    {
        return status switch
        {
            PackageStatus.Created => "已创建",
            PackageStatus.Success => "分拣成功",
            PackageStatus.Failed => "分拣失败",
            PackageStatus.WaitingForLoading => "等待上传",
            PackageStatus.LoadingRejected => "上传拒绝",
            PackageStatus.LoadingSuccess => "上传成功",
            PackageStatus.LoadingTimeout => "上传超时",
            PackageStatus.Error => "异常",
            PackageStatus.Timeout => "超时",
            PackageStatus.Offline => "离线",
            _ => status.ToString()
        };
    }
}
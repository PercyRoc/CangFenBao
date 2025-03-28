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
using SangNeng.Models;
using Serilog;
using static Common.Models.Package.PackageStatus;

namespace Presentation_SangNeng.ViewModels.Windows;

/// <summary>
///     历史记录查询窗口视图模型
/// </summary>
public class HistoryWindowViewModel : BindableBase, IDialogAware
{
    private readonly INotificationService _notificationService;
    private readonly IPackageDataService _packageDataService;
    private DateTime _endDate = DateTime.Today;

    private bool _isLoading;
    private ObservableCollection<PackageRecord> _packageRecords = [];
    private string _searchBarcode = string.Empty;
    private StatusOption? _selectedStatus;
    private DateTime _startDate = DateTime.Today;

    /// <summary>
    ///     构造函数
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
        ExportToExcelCommand = new DelegateCommand(ExecuteExportToExcel, CanExportToExcel)
            .ObservesProperty(() => PackageRecords.Count);
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
    ///     状态列表
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
    ///     选中的状态
    /// </summary>
    public StatusOption? SelectedStatus
    {
        get => _selectedStatus;
        set => SetProperty(ref _selectedStatus, value);
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
    ///     查看图片命令
    /// </summary>
    public ICommand ViewImageCommand { get; }

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

    public string Title => "Package History";

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
            Log.Information("历史记录查询参数 - 开始日期: {StartDate}, 结束日期: {EndDate}, 状态: {Status}, 条码: {Barcode}",
                startTime, endTime, SelectedStatus?.Status, SearchBarcode);

            // 显示加载状态
            IsLoading = true;

            // 检查特定的表中是否有数据(调试用)
            if (StartDate.Date == EndDate.Date)
            {
                var tableRecords = await CheckTableDataAsync(StartDate);
                if (tableRecords.Count > 0)
                    Log.Information("表 Packages_{Date} 中有 {Count} 条数据, 但查询条件可能过滤了它们",
                        StartDate.ToString("yyyyMMdd"), tableRecords.Count);
            }

            // 获取基础查询结果
            List<PackageRecord> records;
            if (SelectedStatus?.Status == null)
                // 查询所有状态
                records = await _packageDataService.GetPackagesInTimeRangeAsync(startTime, endTime);
            else
                // 查询指定状态 - 使用日期范围而不是仅EndDate
                records = await GetPackagesByStatusInDateRangeAsync(SelectedStatus.Status.Value, startTime, endTime);

            // 如果有条码搜索条件，进行过滤
            if (!string.IsNullOrWhiteSpace(SearchBarcode))
                records = records.Where(r => r.Barcode.Contains(SearchBarcode, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            PackageRecords = [.. records];
            _notificationService.ShowSuccess("查询成功");
        }
        catch (Exception ex)
        {
            // 记录错误
            Log.Error(ex, "查询历史记录时发生错误");

            // 通知用户
            _notificationService.ShowError("查询失败");

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
    ///     检查特定日期的表中是否有数据，不做任何过滤（调试用）
    /// </summary>
    private async Task<List<PackageRecord>> CheckTableDataAsync(DateTime date)
    {
        try
        {
            var records = await _packageDataService.GetRawTableDataAsync(date);
            if (records.Count > 0)
            {
                // 打印表中的一些数据用于调试
                var sample = records.Take(3).ToList();
                foreach (var record in sample)
                    Log.Information("表中数据示例 - ID: {Id}, 条码: {Barcode}, 创建时间: {CreateTime}, 状态: {Status}",
                        record.Id, record.Barcode, record.CreateTime, record.Status);
            }
            else
            {
                Log.Warning("表 Packages_{Date} 中没有数据", date.ToString("yyyyMMdd"));
            }

            return records;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "检查表数据时出错");
            return [];
        }
    }

    /// <summary>
    ///     在日期范围内查询指定状态的包裹
    /// </summary>
    private async Task<List<PackageRecord>> GetPackagesByStatusInDateRangeAsync(PackageStatus status,
        DateTime startTime, DateTime endTime)
    {
        var result = new List<PackageRecord>();
        var currentDate = startTime.Date;

        while (currentDate <= endTime.Date)
        {
            try
            {
                var dayRecords = await _packageDataService.GetPackagesByStatusAsync(status, currentDate);
                var filteredRecords = dayRecords.Where(r => r.CreateTime >= startTime && r.CreateTime <= endTime)
                    .ToList();
                result.AddRange(filteredRecords);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "查询{Date}的状态{Status}记录时发生错误", currentDate.ToString("yyyy-MM-dd"), status);
                // 继续查询其他日期
            }

            currentDate = currentDate.AddDays(1);
        }

        return result.OrderByDescending(p => p.CreateTime).ToList();
    }

    private void ViewImage(PackageRecord? record)
    {
        if (record?.ImagePath == null || !File.Exists(record.ImagePath)) return;
        Process.Start(new ProcessStartInfo(record.ImagePath) { UseShellExecute = true });
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
                Filter = "Excel Files|*.xlsx",
                Title = "Export Package Records",
                FileName = $"PackageRecords_{DateTime.Now:yyyyMMddHHmmss}.xlsx"
            };

            if (dialog.ShowDialog() != true) return;

            // 设置EPPlus许可证
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            using var package = new ExcelPackage();
            var worksheet = package.Workbook.Worksheets.Add("Package Records");

            // 设置表头
            var headers = new[]
            {
                "No.", "Barcode", "Weight(kg)", "Length(cm)", "Width(cm)", "Height(cm)",
                "Volume(cm³)", "Status", "Create Time"
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
                worksheet.Cells[row, 3].Value = record.Weight;
                worksheet.Cells[row, 4].Value = record.Length.HasValue ? Math.Round(record.Length.Value, 1) : null;
                worksheet.Cells[row, 5].Value = record.Width.HasValue ? Math.Round(record.Width.Value, 1) : null;
                worksheet.Cells[row, 6].Value = record.Height.HasValue ? Math.Round(record.Height.Value, 1) : null;
                worksheet.Cells[row, 7].Value = record.Volume;
                worksheet.Cells[row, 8].Value = record.Status.ToString();
                worksheet.Cells[row, 9].Value = record.CreateTime;

                // 设置日期格式
                worksheet.Cells[row, 9].Style.Numberformat.Format = "yyyy-MM-dd HH:mm:ss";
            }

            // 自动调整列宽
            worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();

            // 保存文件
            package.SaveAs(new FileInfo(dialog.FileName));

            _notificationService.ShowSuccess($"Successfully exported {PackageRecords.Count} records");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to export Excel");
            _notificationService.ShowError($"Export failed: {ex.Message}");
        }
    }
}
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Windows.Input;
using Common.Data;
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
public class HistoryWindowViewModel : BindableBase, IDialogAware
{
    private readonly INotificationService _notificationService;
    private readonly IPackageDataService _packageDataService;
    private DateTime _endDate = DateTime.Today;

    private bool _isLoading;
    private ObservableCollection<PackageRecord> _packageRecords = [];
    private string _searchBarcode = string.Empty;
    private string _searchChute = string.Empty;
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
        QueryCommand = new DelegateCommand(QueryAsync);
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
        private set
        {
            // 将毫米转换为厘米
            foreach (var record in value)
            {
                if (record.Length.HasValue)
                {
                    record.Length = record.Length.Value / 10.0;
                }

                if (record.Width.HasValue)
                {
                    record.Width = record.Width.Value / 10.0;
                }

                if (record.Height.HasValue)
                {
                    record.Height = record.Height.Value / 10.0;
                }

                if (record.Volume.HasValue)
                {
                    record.Volume = record.Volume.Value / 1000.0; // 将立方毫米转换为立方厘米
                }
            }

            SetProperty(ref _packageRecords, value);
        }
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
            Log.Information("历史记录查询参数 - 开始日期: {StartDate}, 结束日期: {EndDate}, 条码: {Barcode}, 格口: {Chute}",
                startTime, endTime, SearchBarcode, SearchChute);

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
                records = records.Where(r => r.ChuteName == chuteNumber).ToList();
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
                    Log.Information("表中数据示例 - ID: {Id}, 条码: {Barcode}, 创建时间: {CreateTime}, 状态: {Status}, 格口: {Chute}",
                        record.Id, record.Barcode, record.CreateTime, record.Status, record.ChuteName);
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
            return new List<PackageRecord>();
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
                "体积(cm³)", "状态", "创建时间"
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
                worksheet.Cells[row, 3].Value = record.ChuteName;
                worksheet.Cells[row, 4].Value = record.Weight;
                worksheet.Cells[row, 5].Value = record.Length.HasValue ? Math.Round(record.Length.Value, 1) : null;
                worksheet.Cells[row, 6].Value = record.Width.HasValue ? Math.Round(record.Width.Value, 1) : null;
                worksheet.Cells[row, 7].Value = record.Height.HasValue ? Math.Round(record.Height.Value, 1) : null;
                worksheet.Cells[row, 8].Value = record.Volume;
                worksheet.Cells[row, 9].Value = record.Status.ToString();
                worksheet.Cells[row, 10].Value = record.CreateTime;

                // 设置日期格式
                worksheet.Cells[row, 10].Style.Numberformat.Format = "yyyy-MM-dd HH:mm:ss";
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
}
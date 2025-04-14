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

namespace SangNeng.ViewModels.Windows;

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
    private string _selectedStatus = "All";
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
    public List<string> StatusList { get; } = ["All", "Success", "Failed", "Waiting"];

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

                // 设置状态显示
                record.StatusDisplay = GetStatusDisplay(record.Status);
            }

            SetProperty(ref _packageRecords, value);
        }
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
            Log.Information("History query parameters - Start: {StartDate}, End: {EndDate}, Barcode: {Barcode}, Chute: {Chute}, Status: {Status}",
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
            if (SelectedStatus != "All")
            {
                records = records.Where(r => 
                {
                    return SelectedStatus switch
                    {
                        "Success" => r.Status == MeasureSuccess || r.Status == SortSuccess,
                        "Failed" => r.Status == MeasureFailed || r.Status == Error,
                        "Waiting" => r.Status == Measuring || r.Status == Created,
                        _ => true
                    };
                }).ToList();
            }

            PackageRecords = [.. records];
            _notificationService.ShowSuccess("Query successful");
        }
        catch (Exception ex)
        {
            // 记录错误
            Log.Error(ex, "Error querying history records");

            // 通知用户
            _notificationService.ShowError("Query failed");

            // 确保有一个空的结果集
            PackageRecords = [];
        }
        finally
        {
            // 无论成功失败，都结束加载状态
            IsLoading = false;
        }
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
                "No.", "Barcode", "Chute", "Weight(kg)", "Length(cm)", "Width(cm)", "Height(cm)",
                "Volume(cm³)", "Status", "Note", "Create Time"
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

            _notificationService.ShowSuccess($"Successfully exported {PackageRecords.Count} records");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to export Excel");
            _notificationService.ShowError($"Export failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     获取状态的英文显示
    /// </summary>
    private static string GetStatusDisplay(PackageStatus status)
    {
        return status switch
        {
            Created => "Created",
            Measuring => "Measuring",
            MeasureSuccess => "Success",
            MeasureFailed => "Failed",
            Weighing => "Weighing",
            WeighSuccess => "Weigh Success",
            WeighFailed => "Weigh Failed",
            WaitingForChute => "Waiting For Chute",
            _ => status.ToString()
        };
    }
}
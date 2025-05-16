using Common.Data;
using Common.Services.Ui;
using Microsoft.Win32;
using Serilog;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Common.Models.Package;
using static Common.Models.Package.PackageStatus;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace Sunnen.ViewModels.Windows;

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
        // 初始化 RequestClose 属性
        RequestClose = new DialogCloseListener();

        QueryCommand = new DelegateCommand(QueryAsync);
        ViewImageCommand = new DelegateCommand<string?>(ViewImage, CanViewImage)
            .ObservesProperty(() => PackageRecords.Count);
        ExportToExcelCommand = new DelegateCommand(ExecuteExportToExcel, CanExportToExcel)
            .ObservesProperty(() => PackageRecords.Count)
            .ObservesProperty(() => IsLoading);
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
    public List<string> StatusList { get; } = ["All", "Success", "Failed"];

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
        private set => SetProperty(ref _packageRecords, value);
    }

    /// <summary>
    ///     查询命令
    /// </summary>
    public ICommand QueryCommand { get; }

    /// <summary>
    ///     查看图片命令
    /// </summary>
    public DelegateCommand<string?> ViewImageCommand { get; }

    /// <summary>
    ///     导出Excel命令
    /// </summary>
    public DelegateCommand ExportToExcelCommand { get; }

    /// <summary>
    ///     是否正在加载
    /// </summary>
    private bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (SetProperty(ref _isLoading, value))
            {
                // 当 IsLoading 变化时，更新 ExportToExcelCommand 的状态
                ExportToExcelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Title => "Package History";

    // Prism 9.0+ 要求
    public DialogCloseListener RequestClose { get; }

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
            Log.Information(
                "History query parameters - Start: {StartDate}, End: {EndDate}, Barcode: {Barcode}, Chute: {Chute}, Status: {Status}",
                startTime, endTime, SearchBarcode, SearchChute, SelectedStatus);

            // 显示加载状态
            IsLoading = true;

            // 获取基础查询结果
            var records = await _packageDataService.GetPackagesInTimeRangeAsync(startTime, endTime);

            // 如果有条码搜索条件，进行过滤
            if (!string.IsNullOrWhiteSpace(SearchBarcode))
            {
                records = records.Where(r =>
                        r.Barcode.Contains(SearchBarcode, StringComparison.OrdinalIgnoreCase))
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
                        "Success" => r.Status == Success,
                        "Failed" => r.Status is Failed or PackageStatus.Error,
                        _ => true // "All" or unexpected value
                    };
                }).ToList(); // 确保这里也执行 ToList
            }

            PackageRecords = new ObservableCollection<PackageRecord>(records);
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
            // 更新命令状态，因为 PackageRecords 可能已更改
            ViewImageCommand.RaiseCanExecuteChanged(); // 如果 ViewImageCommand 依赖于选中项等，也应更新
        }
    }

    private bool CanViewImage(string? imagePath)
    {
        return !string.IsNullOrEmpty(imagePath);
    }

    private void ViewImage(string? imagePath)
    {
        if (!CanViewImage(imagePath) || imagePath == null)
        {
            Log.Warning("ViewImage executed with invalid path: {ImagePath}", imagePath ?? "<null>");
            _notificationService.ShowWarning("Invalid image path.");
            return;
        }

        try
        {
            if (File.Exists(imagePath))
            {
                // 使用 ShellExecute 打开文件，让操作系统决定如何处理
                var psi = new ProcessStartInfo(imagePath) { UseShellExecute = true };
                Process.Start(psi);
            }
            else
            {
                Log.Error("Image file not found: {ImagePath}", imagePath);
                _notificationService.ShowErrorWithToken($"Image file not found: {imagePath}", "HistoryWindowGrowl");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open image file: {ImagePath}", imagePath);
            _notificationService.ShowErrorWithToken($"Cannot open image: {ex.Message}", "HistoryWindowGrowl");
        }
    }

    /// <summary>
    ///     判断是否可以导出Excel
    /// </summary>
    private bool CanExportToExcel()
    {
        return PackageRecords.Count > 0 && !IsLoading;
    }

    /// <summary>
    ///     使用 NPOI 导出 Excel
    /// </summary>
    private void ExecuteExportToExcel()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Excel Files|*.xlsx",
            Title = "Export Package Records",
            FileName = $"PackageRecords_{DateTime.Now:yyyyMMddHHmmss}.xlsx"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var workbook = new XSSFWorkbook();
            var sheet = workbook.CreateSheet("Package Records");

            // --- 创建表头 --- 
            var headerRow = sheet.CreateRow(0);
            var headers = new[]
            {
                "No.", "Barcode", "Chute", "称重模块(kg)", "Length(cm)", "Width(cm)", "Height(cm)",
                "Volume(cm³)", "Pallet Name", "Status", "Note", "Create Time"
            };

            // --- 设置表头样式 --- 
            var headerCellStyle = workbook.CreateCellStyle();
            var headerFont = workbook.CreateFont();
            headerFont.IsBold = true;
            headerCellStyle.SetFont(headerFont);
            headerCellStyle.Alignment = HorizontalAlignment.Center;
            headerCellStyle.VerticalAlignment = VerticalAlignment.Center;
            // 设置边框
            headerCellStyle.BorderBottom = BorderStyle.Thin;
            headerCellStyle.BorderLeft = BorderStyle.Thin;
            headerCellStyle.BorderRight = BorderStyle.Thin;
            headerCellStyle.BorderTop = BorderStyle.Thin;
            // 设置背景色 (NPOI 使用 IndexedColors)
            // headerCellStyle.FillForegroundColor = IndexedColors.Grey25Percent.Index; 
            // headerCellStyle.FillPattern = FillPattern.SolidForeground;

            for (var i = 0; i < headers.Length; i++)
            {
                var cell = headerRow.CreateCell(i);
                cell.SetCellValue(headers[i]);
                cell.CellStyle = headerCellStyle;
            }

            // --- 创建日期单元格样式 --- 
            var dateCellStyle = workbook.CreateCellStyle();
            var dataFormat = workbook.CreateDataFormat();
            dateCellStyle.DataFormat = dataFormat.GetFormat("yyyy-MM-dd HH:mm:ss");

            // --- 写入数据 --- 
            for (var i = 0; i < PackageRecords.Count; i++)
            {
                var record = PackageRecords[i];
                var dataRow = sheet.CreateRow(i + 1); // 数据从第二行开始 (索引为1)

                dataRow.CreateCell(0).SetCellValue(record.Id); // No.
                dataRow.CreateCell(1).SetCellValue(record.Barcode);
                dataRow.CreateCell(2).SetCellValue(record.ChuteNumber ?? 0); // Chute (处理可能的 null)
                dataRow.CreateCell(3).SetCellValue(record.Weight); // 称重模块
                dataRow.CreateCell(4)
                    .SetCellValue(record.Length.HasValue
                        ? Math.Round(record.Length.Value, 1)
                        : double.NaN); // Length (使用 NaN 表示空值)
                dataRow.CreateCell(5)
                    .SetCellValue(record.Width.HasValue ? Math.Round(record.Width.Value, 1) : double.NaN); // Width
                dataRow.CreateCell(6)
                    .SetCellValue(record.Height.HasValue ? Math.Round(record.Height.Value, 1) : double.NaN); // Height
                dataRow.CreateCell(7)
                    .SetCellValue(record.Volume.HasValue ? Math.Round(record.Volume.Value, 1) : double.NaN); // Volume
                dataRow.CreateCell(8).SetCellValue(record.PalletName);
                dataRow.CreateCell(9).SetCellValue(record.StatusDisplay);
                dataRow.CreateCell(10).SetCellValue(record.ErrorMessage);

                var dateCell = dataRow.CreateCell(11);

                dateCell.SetCellValue(record.CreateTime);
                dateCell.CellStyle = dateCellStyle; // 应用日期格式
            }

            // --- 自动调整列宽 --- (在填充数据后执行)
            for (int i = 0; i < headers.Length; i++)
            {
                sheet.AutoSizeColumn(i);
            }

            // --- 保存文件 --- 
            using (var stream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write))
            {
                workbook.Write(stream);
            }

            _notificationService.ShowSuccess($"Successfully exported {PackageRecords.Count} records");
            Log.Information("Successfully exported {Count} records to {FilePath}", PackageRecords.Count,
                dialog.FileName);
        }
        catch (IOException ioEx)
        {
            Log.Error(ioEx, "Failed to export Excel - file access issue");
            _notificationService.ShowError(
                $"Export failed: Could not access file. Is it open in another program? ({ioEx.Message})");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to export Excel");
            _notificationService.ShowError($"Export failed: {ex.Message}");
        }
    }
}
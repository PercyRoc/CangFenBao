using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Common.Data;
using Common.Models.Package;
using Common.Services.Ui;
using Microsoft.Win32;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
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

    public DialogCloseListener RequestClose { get; private set; } = default!;

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
    ///     导出Excel (使用 NPOI)
    /// </summary>
    private async void ExecuteExportToExcel()
    {
        if (!CanExportToExcel()) return;

        var dialog = new SaveFileDialog
        {
            Filter = "Excel文件|*.xlsx",
            Title = "导出包裹记录",
            FileName = $"包裹记录_{DateTime.Now:yyyyMMddHHmmss}.xlsx"
        };

        if (dialog.ShowDialog() != true) return;
        var filePath = dialog.FileName;

        IsLoading = true;

        try
        {
            await Task.Run(() =>
            {
                IWorkbook workbook = new XSSFWorkbook();
                ISheet worksheet = workbook.CreateSheet("包裹记录");

                // 创建字体和样式
                IFont headerFont = workbook.CreateFont();
                headerFont.IsBold = true;

                ICellStyle headerStyle = workbook.CreateCellStyle();
                headerStyle.SetFont(headerFont);
                headerStyle.FillForegroundColor = IndexedColors.Grey25Percent.Index; // 使用预定义颜色
                headerStyle.FillPattern = FillPattern.SolidForeground;
                headerStyle.BorderBottom = BorderStyle.Thin;
                headerStyle.BorderLeft = BorderStyle.Thin;
                headerStyle.BorderRight = BorderStyle.Thin;
                headerStyle.BorderTop = BorderStyle.Thin;
                headerStyle.Alignment = HorizontalAlignment.Center;
                headerStyle.VerticalAlignment = VerticalAlignment.Center;

                // 日期格式
                IDataFormat dataFormat = workbook.CreateDataFormat();
                ICellStyle dateStyle = workbook.CreateCellStyle();
                dateStyle.DataFormat = dataFormat.GetFormat("yyyy-MM-dd HH:mm:ss");

                // 设置表头
                var headers = new[]
                {
                    "编号", "条码", "格口", "重量(kg)", "长度(cm)", "宽度(cm)", "高度(cm)",
                    "体积(cm³)", "状态", "备注", "创建时间"
                };

                IRow headerRow = worksheet.CreateRow(0);
                for (var i = 0; i < headers.Length; i++)
                {
                    ICell cell = headerRow.CreateCell(i);
                    cell.SetCellValue(headers[i]);
                    cell.CellStyle = headerStyle;
                }

                // 写入数据
                var recordsToExport = PackageRecords.ToList();
                for (var i = 0; i < recordsToExport.Count; i++)
                {
                    var record = recordsToExport[i];
                    var row = worksheet.CreateRow(i + 1); // NPOI 行索引从 0 开始

                    row.CreateCell(0).SetCellValue(record.Id);
                    row.CreateCell(1).SetCellValue(record.Barcode);

                    // Handle nullable int? for ChuteNumber
                    ICell chuteCell = row.CreateCell(2);
                    if (record.ChuteNumber.HasValue) chuteCell.SetCellValue(record.ChuteNumber.Value); else chuteCell.SetCellType(CellType.Blank);

                    // Handle double for Weight (non-nullable)
                    row.CreateCell(3).SetCellValue(record.Weight);

                    // Handle nullable double? for Length, Width, Height
                    ICell lengthCell = row.CreateCell(4);
                    if (record.Length.HasValue) lengthCell.SetCellValue(Math.Round(record.Length.Value, 1)); else lengthCell.SetCellType(CellType.Blank);

                    ICell widthCell = row.CreateCell(5);
                    if (record.Width.HasValue) widthCell.SetCellValue(Math.Round(record.Width.Value, 1)); else widthCell.SetCellType(CellType.Blank);

                    ICell heightCell = row.CreateCell(6);
                    if (record.Height.HasValue) heightCell.SetCellValue(Math.Round(record.Height.Value, 1)); else heightCell.SetCellType(CellType.Blank);

                    // Handle nullable double? for Volume
                    ICell volumeCell = row.CreateCell(7);
                    if (record.Volume.HasValue) volumeCell.SetCellValue(record.Volume.Value); else volumeCell.SetCellType(CellType.Blank);

                    row.CreateCell(8).SetCellValue(record.StatusDisplay);
                    row.CreateCell(9).SetCellValue(record.ErrorMessage);

                    ICell dateCell = row.CreateCell(10);
                    dateCell.SetCellValue(record.CreateTime);
                    dateCell.CellStyle = dateStyle; // 应用日期样式
                }

                // 自动调整列宽
                for (int i = 0; i < headers.Length; i++)
                {
                    worksheet.AutoSizeColumn(i);
                }

                // 保存文件
                using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                {
                    workbook.Write(fs);
                }
            }); // Task.Run 结束

            _notificationService.ShowSuccessWithToken($"已成功导出 {PackageRecords.Count} 条记录到 {Path.GetFileName(filePath)}", "HistoryWindowGrowl");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "使用NPOI导出Excel失败");
            _notificationService.ShowErrorWithToken($"导出失败: {ex.Message}", "HistoryWindowGrowl");
        }
        finally
        {
            IsLoading = false;
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
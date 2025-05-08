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
using WPFLocalizeExtension.Engine;

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
    private string _selectedStatus;

    /// <summary>
    ///     状态列表 - 动态生成
    /// </summary>
    public List<string> StatusList { get; }

    /// <summary>
    ///     选中的状态 (Localized string from WPFLocalizeExtension)
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

        // 动态生成状态列表 - 使用本地化
        var allOption = GetLocString("HistoryDialog_All", "All");
        var statusOptions = new List<string> { allOption };
        var displayNames = Enum.GetValues<PackageStatus>()
                               .Select(GetLocalizedStatusDisplayForFilter)
                               .Distinct()
                               .OrderBy(s => s);
        statusOptions.AddRange(displayNames);
        StatusList = statusOptions;
        _selectedStatus = allOption;

        QueryCommand = new DelegateCommand(QueryAsync);
        ExportToExcelCommand = new DelegateCommand(ExecuteExportToExcel, CanExportToExcel)
            .ObservesProperty(() => PackageRecords.Count);
        OpenImageCommand = new DelegateCommand<string>(OpenImageExecute, CanOpenImage);
    }

    private static string GetLocString(string key, string? fallback = null)
    {
        var value = LocalizeDictionary.Instance.DefaultProvider?.GetLocalizedObject(key, null, LocalizeDictionary.Instance.Culture) as string;
        return value ?? fallback ?? key;
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
            var allOptionLocalized = GetLocString("HistoryDialog_All", "All");

            Log.Information("历史记录查询参数 - 开始日期: {StartDate}, 结束日期: {EndDate}, 条码: {Barcode}, 格口: {Chute}, 状态: {Status}",
                startTime, endTime, SearchBarcode, SearchChute, SelectedStatus);

            IsLoading = true;
            var records = await _packageDataService.GetPackagesInTimeRangeAsync(startTime, endTime);

            if (!string.IsNullOrWhiteSpace(SearchBarcode))
            {
                records = records.Where(r => r.Barcode.Contains(SearchBarcode, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (!string.IsNullOrWhiteSpace(SearchChute) && int.TryParse(SearchChute, out var chuteNumber))
            {
                records = records.Where(r => r.ChuteNumber == chuteNumber).ToList();
            }

            if (SelectedStatus != allOptionLocalized)
            {
                var targetStatuses = Enum.GetValues<PackageStatus>()
                                         .Where(s => GetLocalizedStatusDisplayForFilter(s) == SelectedStatus)
                                         .ToList();
                if (targetStatuses.Any())
                {
                    records = records.Where(r => targetStatuses.Contains(r.Status)).ToList();
                }
                else
                {
                    Log.Warning("无法将选择的状态 '{SelectedStatus}' 映射到任何 PackageStatus。将显示所有状态。", SelectedStatus);
                }
            }
            
            PackageRecords = [.. records];
            _notificationService.ShowSuccessWithToken(
                GetLocString("HistoryDialog_QuerySuccess", "Query successful"),
                "HistoryWindowGrowl");
        }
        catch (Exception ex)
        {             Log.Error(ex, "查询历史记录时发生错误");
            _notificationService.ShowErrorWithToken(
                GetLocString("HistoryDialog_QueryFailed", "Query failed"),
                "HistoryWindowGrowl");
            PackageRecords = [];
        }
        finally
        {             IsLoading = false;
            ExportToExcelCommand.RaiseCanExecuteChanged();
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
            Filter = GetLocString("HistoryDialog_ExportFilter", "Excel Files|*.xlsx"),
            Title = GetLocString("HistoryDialog_ExportTitle", "Export Package Records"),
            FileName = $"{GetLocString("HistoryDialog_ExportFileNamePrefix", "PackageRecords")}_{DateTime.Now:yyyyMMddHHmmss}.xlsx"
        };

        if (dialog.ShowDialog() != true) return;
        var filePath = dialog.FileName;

        IsLoading = true;

        try
        {
            await Task.Run(() =>
            {
                var workbook = new XSSFWorkbook();
                var worksheet = workbook.CreateSheet(GetLocString("HistoryDialog_ExportSheetName", "Package Records"));

                var headerFont = workbook.CreateFont();
                headerFont.IsBold = true;

                var headerStyle = workbook.CreateCellStyle();
                headerStyle.SetFont(headerFont);
                headerStyle.FillForegroundColor = IndexedColors.Grey25Percent.Index;
                headerStyle.FillPattern = FillPattern.SolidForeground;
                headerStyle.BorderBottom = BorderStyle.Thin;
                headerStyle.BorderLeft = BorderStyle.Thin;
                headerStyle.BorderRight = BorderStyle.Thin;
                headerStyle.BorderTop = BorderStyle.Thin;
                headerStyle.Alignment = HorizontalAlignment.Center;
                headerStyle.VerticalAlignment = VerticalAlignment.Center;

                var dataFormat = workbook.CreateDataFormat();
                var dateStyle = workbook.CreateCellStyle();
                dateStyle.DataFormat = dataFormat.GetFormat("yyyy-MM-dd HH:mm:ss");

                var headers = new[]
                {
                    GetLocString("HistoryDialog_Header_Id", "ID"),
                    GetLocString("HistoryDialog_Header_Barcode", "Barcode"),
                    GetLocString("HistoryDialog_Header_Chute", "Chute"),
                    GetLocString("HistoryDialog_Header_Weight", "Weight(kg)"),
                    GetLocString("HistoryDialog_Header_Length", "Length(cm)"),
                    GetLocString("HistoryDialog_Header_Width", "Width(cm)"),
                    GetLocString("HistoryDialog_Header_Height", "Height(cm)"),
                    GetLocString("HistoryDialog_Header_Volume", "Volume(cm³)"),
                    GetLocString("HistoryDialog_Header_Status", "Status"),
                    GetLocString("HistoryDialog_Header_Remarks", "Remarks"),
                    GetLocString("HistoryDialog_Header_CreateTime", "Create Time")
                };

                var headerRow = worksheet.CreateRow(0);
                for (var i = 0; i < headers.Length; i++)
                {
                    ICell cell = headerRow.CreateCell(i);
                    cell.SetCellValue(headers[i]);
                    cell.CellStyle = headerStyle;
                }

                var recordsToExport = PackageRecords.ToList();
                for (var i = 0; i < recordsToExport.Count; i++)
                {
                    var record = recordsToExport[i];
                    var row = worksheet.CreateRow(i + 1);

                    row.CreateCell(0).SetCellValue(record.Id);
                    row.CreateCell(1).SetCellValue(record.Barcode);
                    var chuteCell = row.CreateCell(2);
                    if (record.ChuteNumber.HasValue) chuteCell.SetCellValue(record.ChuteNumber.Value); else chuteCell.SetCellType(CellType.Blank);
                    row.CreateCell(3).SetCellValue(record.Weight);
                    var lengthCell = row.CreateCell(4);
                    if (record.Length.HasValue) lengthCell.SetCellValue(Math.Round(record.Length.Value, 1)); else lengthCell.SetCellType(CellType.Blank);
                    var widthCell = row.CreateCell(5);
                    if (record.Width.HasValue) widthCell.SetCellValue(Math.Round(record.Width.Value, 1)); else widthCell.SetCellType(CellType.Blank);
                    var heightCell = row.CreateCell(6);
                    if (record.Height.HasValue) heightCell.SetCellValue(Math.Round(record.Height.Value, 1)); else heightCell.SetCellType(CellType.Blank);
                    var volumeCell = row.CreateCell(7);
                    if (record.Volume.HasValue) volumeCell.SetCellValue(record.Volume.Value); else volumeCell.SetCellType(CellType.Blank);

                    row.CreateCell(8).SetCellValue(record.StatusDisplay);
                    row.CreateCell(9).SetCellValue(record.ErrorMessage);

                    var dateCell = row.CreateCell(10);
                    dateCell.SetCellValue(record.CreateTime);
                    dateCell.CellStyle = dateStyle;
                }

                for (var i = 0; i < headers.Length; i++)
                {
                    worksheet.AutoSizeColumn(i);
                }

                using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                workbook.Write(fs);
            });

            var successMsgFormat = GetLocString("HistoryDialog_ExportSuccessFormat", "{0} records exported successfully to {1}");
            _notificationService.ShowSuccessWithToken(string.Format(successMsgFormat, PackageRecords.Count, Path.GetFileName(filePath)), "HistoryWindowGrowl");
        }
        catch (Exception ex)
        {             Log.Error(ex, "使用NPOI导出Excel失败");
            var errorMsgFormat = GetLocString("HistoryDialog_ExportFailedFormat", "Export failed: {0}");
            _notificationService.ShowErrorWithToken(string.Format(errorMsgFormat, ex.Message), "HistoryWindowGrowl");
        }
        finally
        {             IsLoading = false;
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
        if (!CanOpenImage(imagePath) || imagePath == null)
        {
            Log.Warning("Attempted to open image with invalid path: {ImagePath}", imagePath ?? "null");
            return;
        }

        try
        {
            if (File.Exists(imagePath))
            {
                var processStartInfo = new ProcessStartInfo(imagePath) { UseShellExecute = true };
                Process.Start(processStartInfo);
                Log.Information("Opened image file: {ImagePath}", imagePath);
            }
            else
            {
                Log.Error("Image file not found at path: {ImagePath}", imagePath);
                var notFoundMsgFormat = GetLocString("HistoryDialog_ImageNotFoundFormat", "Image file not found: {0}");
                _notificationService.ShowErrorWithToken(string.Format(notFoundMsgFormat, imagePath), "HistoryWindowGrowl");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open image file: {ImagePath}", imagePath);
            var openFailedMsgFormat = GetLocString("HistoryDialog_ImageOpenFailedFormat", "Could not open image: {0}");
            _notificationService.ShowErrorWithToken(string.Format(openFailedMsgFormat, ex.Message), "HistoryWindowGrowl");
        }
    }

    /// <summary>
    ///     获取用于状态筛选器下拉列表的本地化显示名称
    /// </summary>
    private string GetLocalizedStatusDisplayForFilter(PackageStatus status)
    {
        var resourceKey = status switch
        {
            PackageStatus.Created => "HistoryDialog_Status_Created",
            PackageStatus.Success => "HistoryDialog_Status_Success",
            PackageStatus.Failed => "HistoryDialog_Status_Failed",
            PackageStatus.WaitingForLoading => "HistoryDialog_Status_WaitingForLoading",
            PackageStatus.LoadingRejected => "HistoryDialog_Status_LoadingRejected",
            PackageStatus.LoadingSuccess => "HistoryDialog_Status_LoadingSuccess",
            PackageStatus.LoadingTimeout => "HistoryDialog_Status_LoadingTimeout",
            PackageStatus.Error => "HistoryDialog_Status_Error",
            PackageStatus.Timeout => "HistoryDialog_Status_Timeout",
            PackageStatus.Offline => "HistoryDialog_Status_Offline",
            _ => $"HistoryDialog_Status_{status}"
        };
        return GetLocString(resourceKey, status.ToString());
    }
}
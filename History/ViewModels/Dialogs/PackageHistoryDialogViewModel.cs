using System.Collections.ObjectModel;
using System.Windows.Input;
using History.Data;
using Serilog;
using System.Diagnostics;
using System.IO;
using NPOI.XSSF.UserModel;
using Microsoft.Win32;
using WPFLocalizeExtension.Engine;
using History.Configuration;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Common.Models.Package;
using HandyControl.Controls;
using HandyControl.Data;
using JetBrains.Annotations;

namespace History.ViewModels.Dialogs;

public class PackageHistoryDialogViewModel : BindableBase, IDialogAware
{
    private readonly IPackageHistoryDataService _packageHistoryDataService;
    private readonly ILogger _logger;
    private readonly HistoryViewConfiguration? _configuration;
    private List<HistoryColumnSpec>? _effectiveColumnSpecs = new List<HistoryColumnSpec>();

    private bool _isIndexColVisible;
    private bool _isBarcodeColVisible;
    private bool _isCreateTimeColVisible;
    private bool _isStatusDisplayColVisible;
    private bool _isWeightColVisible;
    private bool _isChuteNumberColVisible;
    private bool _isLengthColVisible;
    private bool _isWidthColVisible;
    private bool _isHeightColVisible;
    private bool _isPalletNameColVisible;
    private bool _isPalletWeightColVisible;
    private bool _isImageColVisible;

    public bool IsIndexColVisible { get => _isIndexColVisible;
        private set => SetProperty(ref _isIndexColVisible, value); }
    public bool IsBarcodeColVisible { get => _isBarcodeColVisible;
        private set => SetProperty(ref _isBarcodeColVisible, value); }
    public bool IsCreateTimeColVisible { get => _isCreateTimeColVisible;
        private set => SetProperty(ref _isCreateTimeColVisible, value); }
    public bool IsStatusDisplayColVisible { get => _isStatusDisplayColVisible;
        private set => SetProperty(ref _isStatusDisplayColVisible, value); }
    public bool IsWeightColVisible { get => _isWeightColVisible;
        private set => SetProperty(ref _isWeightColVisible, value); }
    public bool IsChuteNumberColVisible { get => _isChuteNumberColVisible;
        private set => SetProperty(ref _isChuteNumberColVisible, value); }
    public bool IsLengthColVisible { get => _isLengthColVisible;
        private set => SetProperty(ref _isLengthColVisible, value); }
    public bool IsWidthColVisible { get => _isWidthColVisible;
        private set => SetProperty(ref _isWidthColVisible, value); }
    public bool IsHeightColVisible { get => _isHeightColVisible;
        private set => SetProperty(ref _isHeightColVisible, value); }
    public bool IsPalletNameColVisible { get => _isPalletNameColVisible;
        private set => SetProperty(ref _isPalletNameColVisible, value); }
    public bool IsPalletWeightColVisible { get => _isPalletWeightColVisible;
        private set => SetProperty(ref _isPalletWeightColVisible, value); }
    public bool IsImageColVisible { get => _isImageColVisible;
        private set => SetProperty(ref _isImageColVisible, value); }

    private string _title = (LocalizeDictionary.Instance.GetLocalizedObject("History", "Strings", "PackageHistory_Title", LocalizeDictionary.Instance.Culture) as string) ?? "包裹历史记录";
    private DateTime? _startDate = DateTime.Today.AddDays(-7);
    private DateTime? _endDate = DateTime.Today;
    private string? _barcodeFilter;
    private ObservableCollection<PackageHistoryRecord> _historicalPackages = new();
    private bool _isLoading;
    private PackageHistoryRecord? _selectedPackage;

    private int _currentPage = 1;
    private int _pageSize = 50;
    private int _totalItems;
    private int _totalPages;
    private bool _isFirstPage = true;
    private bool _isLastPage = true;
    private string _pagingInfo = string.Empty;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public DateTime? StartDate
    {
        get => _startDate;
        set => SetProperty(ref _startDate, value);
    }

    public DateTime? EndDate
    {
        get => _endDate;
        set => SetProperty(ref _endDate, value);
    }

    public string? BarcodeFilter
    {
        get => _barcodeFilter;
        set => SetProperty(ref _barcodeFilter, value);
    }

    public ObservableCollection<PackageHistoryRecord> HistoricalPackages
    {
        get => _historicalPackages;
        set => SetProperty(ref _historicalPackages, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public PackageHistoryRecord? SelectedPackage
    {
        get => _selectedPackage;
        set => SetProperty(ref _selectedPackage, value);
    }

    private int CurrentPage { get => _currentPage;
        set => SetProperty(ref _currentPage, value, UpdatePagingCommandsAndInfo); }
    public int PageSize { get => _pageSize; set => SetProperty(ref _pageSize, value, () => { CurrentPage = 1; _ = ExecuteSearchCommandAsync(); }); }

    private int TotalItems { get => _totalItems;
        set => SetProperty(ref _totalItems, value); }

    private int TotalPages { get => _totalPages;
        set => SetProperty(ref _totalPages, value, UpdatePagingCommandsAndInfo); }
    public bool IsFirstPage { get => _isFirstPage; private set => SetProperty(ref _isFirstPage, value); }
    public bool IsLastPage { get => _isLastPage; private set => SetProperty(ref _isLastPage, value); }
    public string PagingInfo { get => _pagingInfo; private set => SetProperty(ref _pagingInfo, value); }

    public ICommand SearchCommand { get; }
    public ICommand CloseDialogCommand { get; }
    public ICommand ViewImageCommand { get; }
    public ICommand ExportToExcelCommand { get; }
    public DelegateCommand PreviousPageCommand { get; }
    public DelegateCommand NextPageCommand { get; }

    [UsedImplicitly] public DialogCloseListener RequestClose { get; }

    public PackageHistoryDialogViewModel(IPackageHistoryDataService packageHistoryDataService, ILogger logger, HistoryViewConfiguration? viewConfiguration = null)
    {
        _packageHistoryDataService = packageHistoryDataService ?? throw new ArgumentNullException(nameof(packageHistoryDataService));
        _logger = logger.ForContext<PackageHistoryDialogViewModel>();
        _configuration = viewConfiguration;

        _effectiveColumnSpecs = _configuration?.ColumnSpecs.Count > 0
            ? [.. _configuration.ColumnSpecs.OrderBy(c => c.DisplayOrderInGrid)]
            : [.. GetDefaultColumnSpecs().OrderBy(c => c.DisplayOrderInGrid)];
        UpdateColumnVisibilityFromSpecs();

        SearchCommand = new DelegateCommand(async void () => { CurrentPage = 1; await ExecuteSearchCommandAsync(); });
        CloseDialogCommand = new DelegateCommand(ExecuteCloseDialogCommand);
        ViewImageCommand = new DelegateCommand<PackageHistoryRecord>(ExecuteViewImageCommand, CanExecuteViewImageCommand);
        ExportToExcelCommand = new DelegateCommand(async void () => await ExecuteExportToExcelCommand(), CanExecuteExportToExcelCommand);

        PreviousPageCommand = new DelegateCommand(async void () => await ExecutePreviousPageCommandAsync(), CanExecutePreviousPageCommand);
        NextPageCommand = new DelegateCommand(async void () => await ExecuteNextPageCommandAsync(), CanExecuteNextPageCommand);

        HistoricalPackages.CollectionChanged += (_, _) => RaiseCanExecuteChangedForAllCommands();
        PropertyChanged += OnPropertyChanged;

        UpdatePagingInfo();
    }

    private void RaiseCanExecuteChangedForAllCommands()
    {
        (ExportToExcelCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        PreviousPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
        (ViewImageCommand as DelegateCommand<PackageHistoryRecord>)?.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectedPackage))
        {
            (ViewImageCommand as DelegateCommand<PackageHistoryRecord>)?.RaiseCanExecuteChanged();
        }
        if (e.PropertyName == nameof(IsLoading))
        {
            RaiseCanExecuteChangedForAllCommands();
        }
    }

    private void UpdatePagingCommandsAndInfo()
    {
        IsFirstPage = CurrentPage <= 1;
        IsLastPage = CurrentPage >= TotalPages;
        PreviousPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
        UpdatePagingInfo();
    }

    private void UpdatePagingInfo()
    {
        if (TotalItems == 0)
        {
            PagingInfo = GetLocalizedString("PackageHistory_Paging_NoRecords");
        }
        else
        {
            var firstItem = (CurrentPage - 1) * PageSize + 1;
            var lastItem = Math.Min(CurrentPage * PageSize, TotalItems);
            PagingInfo = GetLocalizedString("PackageHistory_Paging_Info", CurrentPage, TotalPages, TotalItems, firstItem, lastItem);
        }
    }

    private void UpdateColumnVisibilityFromSpecs()
    {
        if (_effectiveColumnSpecs == null || !_effectiveColumnSpecs.Any())
        {
            _effectiveColumnSpecs = GetDefaultColumnSpecs().OrderBy(c => c.DisplayOrderInGrid).ToList();
        }

        IsIndexColVisible = _effectiveColumnSpecs.Any(c => c.PropertyName == nameof(PackageHistoryRecord.Index) && c.IsDisplayed);
        IsBarcodeColVisible = _effectiveColumnSpecs.Any(c => c.PropertyName == nameof(PackageHistoryRecord.Barcode) && c.IsDisplayed);
        IsCreateTimeColVisible = _effectiveColumnSpecs.Any(c => c.PropertyName == nameof(PackageHistoryRecord.CreateTime) && c.IsDisplayed);
        IsStatusDisplayColVisible = _effectiveColumnSpecs.Any(c => c.PropertyName == nameof(PackageHistoryRecord.Status) && c.IsDisplayed);
        IsWeightColVisible = _effectiveColumnSpecs.Any(c => c.PropertyName == nameof(PackageHistoryRecord.Weight) && c.IsDisplayed);
        IsChuteNumberColVisible = _effectiveColumnSpecs.Any(c => c.PropertyName == nameof(PackageHistoryRecord.ChuteNumber) && c.IsDisplayed);
        IsLengthColVisible = _effectiveColumnSpecs.Any(c => c.PropertyName == nameof(PackageHistoryRecord.Length) && c.IsDisplayed);
        IsWidthColVisible = _effectiveColumnSpecs.Any(c => c.PropertyName == nameof(PackageHistoryRecord.Width) && c.IsDisplayed);
        IsHeightColVisible = _effectiveColumnSpecs.Any(c => c.PropertyName == nameof(PackageHistoryRecord.Height) && c.IsDisplayed);
        IsPalletNameColVisible = _effectiveColumnSpecs.Any(c => c.PropertyName == nameof(PackageHistoryRecord.PalletName) && c.IsDisplayed);
        IsPalletWeightColVisible = _effectiveColumnSpecs.Any(c => c.PropertyName == nameof(PackageHistoryRecord.PalletWeight) && c.IsDisplayed);
        IsImageColVisible = _effectiveColumnSpecs.Any(c => c.PropertyName == "ImageAction" && c.IsDisplayed);
    }

    private List<HistoryColumnSpec> GetDefaultColumnSpecs()
    {
        return
        [
            new()
            {
                PropertyName = nameof(PackageHistoryRecord.Index), HeaderResourceKey = "PackageHistory_Header_Index",
                IsDisplayed = true, IsExported = true, DisplayOrderInGrid = 0, DisplayOrderInExcel = 0, Width = "Auto"
            },
            new()
            {
                PropertyName = nameof(PackageHistoryRecord.Barcode),
                HeaderResourceKey = "PackageHistory_Header_Barcode", IsDisplayed = true, IsExported = true,
                DisplayOrderInGrid = 2, DisplayOrderInExcel = 2, Width = "*"
            },
            new()
            {
                PropertyName = nameof(PackageHistoryRecord.CreateTime),
                HeaderResourceKey = "PackageHistory_Header_CreateTime", IsDisplayed = true, IsExported = true,
                DisplayOrderInGrid = 6, DisplayOrderInExcel = 5, Width = "*", StringFormat = "yyyy-MM-dd HH:mm:ss"
            },
            new()
            {
                PropertyName = nameof(PackageHistoryRecord.Status), HeaderResourceKey = "PackageHistory_Header_Status",
                IsDisplayed = true, IsExported = true, DisplayOrderInGrid = 7, DisplayOrderInExcel = 6, Width = "*"
            },
            new()
            {
                PropertyName = nameof(PackageHistoryRecord.Weight), HeaderResourceKey = "PackageHistory_Header_Weight",
                IsDisplayed = true, IsExported = true, DisplayOrderInGrid = 8, DisplayOrderInExcel = 7, Width = "*",
                StringFormat = "F3"
            },
            new()
            {
                PropertyName = nameof(PackageHistoryRecord.ChuteNumber),
                HeaderResourceKey = "PackageHistory_Header_ChuteNumber", IsDisplayed = true, IsExported = true,
                DisplayOrderInGrid = 12, DisplayOrderInExcel = 11, Width = "*"
            },
            new()
            {
                PropertyName = nameof(PackageHistoryRecord.Length), HeaderResourceKey = "PackageHistory_Header_Length",
                IsDisplayed = false, IsExported = true, DisplayOrderInGrid = 9, DisplayOrderInExcel = 8, Width = "*",
                StringFormat = "F1"
            },
            new()
            {
                PropertyName = nameof(PackageHistoryRecord.Width), HeaderResourceKey = "PackageHistory_Header_Width",
                IsDisplayed = false, IsExported = true, DisplayOrderInGrid = 10, DisplayOrderInExcel = 9, Width = "*",
                StringFormat = "F1"
            },
            new()
            {
                PropertyName = nameof(PackageHistoryRecord.Height), HeaderResourceKey = "PackageHistory_Header_Height",
                IsDisplayed = false, IsExported = true, DisplayOrderInGrid = 11, DisplayOrderInExcel = 10, Width = "*",
                StringFormat = "F1"
            },
            new()
            {
                PropertyName = nameof(PackageHistoryRecord.PalletName),
                HeaderResourceKey = "PackageHistory_Header_PalletName", IsDisplayed = true, IsExported = true,
                DisplayOrderInGrid = 110, DisplayOrderInExcel = 110
            },
            new()
            {
                PropertyName = nameof(PackageHistoryRecord.PalletWeight),
                HeaderResourceKey = "PackageHistory_Header_PalletWeight", IsDisplayed = false, IsExported = true,
                DisplayOrderInGrid = 19, DisplayOrderInExcel = 18, Width = "*", StringFormat = "F3"
            },
            new()
            {
                PropertyName = "ImageAction", HeaderResourceKey = "PackageHistory_Header_ImageAction",
                IsDisplayed = true, IsExported = false, DisplayOrderInGrid = 4, Width = "Auto", IsTemplateColumn = true
            },
            new()
            {
                PropertyName = nameof(PackageHistoryRecord.ImagePath),
                HeaderResourceKey = "PackageHistory_Header_ImagePath", IsDisplayed = false, IsExported = true,
                DisplayOrderInGrid = 5, DisplayOrderInExcel = 4
            }
        ];
    }

    private async Task ExecuteSearchCommandAsync()
    {
        _logger.Debug("ExecuteSearchCommandAsync: Starting search. Current IsLoading state: {IsLoadingState}", IsLoading);
        IsLoading = true;
        _logger.Debug("ExecuteSearchCommandAsync: IsLoading set to true.");
        HistoricalPackages.Clear();
        try
        {
            _logger.Debug("ExecuteSearchCommandAsync: Calling _packageHistoryDataService.GetPackagesAsync with StartDate={StartDate}, EndDate={EndDate}, BarcodeFilter={BarcodeFilter}, Page={CurrentPage}, PageSize={PageSize}", 
                StartDate, EndDate, BarcodeFilter, CurrentPage, PageSize);
            var result = await _packageHistoryDataService.GetPackagesAsync(StartDate, EndDate, BarcodeFilter, CurrentPage, PageSize);
            _logger.Debug("ExecuteSearchCommandAsync: GetPackagesAsync returned. TotalCount: {TotalCount}, Records Fetched: {RecordCount}", result.TotalCount, result.Records.Count());
            
            TotalItems = result.TotalCount;
            TotalPages = (int)Math.Ceiling((double)TotalItems / PageSize);
            if (TotalPages == 0 && TotalItems > 0) TotalPages = 1;
            // Ensure CurrentPage is within valid range after TotalPages is calculated
            if (CurrentPage > TotalPages && TotalPages > 0) 
            {
                _logger.Debug("ExecuteSearchCommandAsync: CurrentPage {OldCurrentPage} was out of sync, resetting to TotalPages {NewTotalPages}", CurrentPage, TotalPages);
                CurrentPage = TotalPages; // This might trigger another search if not handled carefully, but our command is manual.
            } 
            else if (CurrentPage == 0 && TotalPages > 0) // Handle case where current page might be 0 if total pages was 0 previously
            {
                _logger.Debug("ExecuteSearchCommandAsync: CurrentPage was 0, resetting to 1 as TotalPages is now {NewTotalPages}", TotalPages);
                CurrentPage = 1;
            }

            HistoricalPackages.Clear(); // Clearing again just to be safe if there was a re-entrant call somehow (should not happen with current logic)
            foreach (var package in result.Records)
            {
                HistoricalPackages.Add(package);
            }
            _logger.Debug("ExecuteSearchCommandAsync: Populated HistoricalPackages with {Count} items.", HistoricalPackages.Count);

            if (TotalItems > 0)
            {
                // Growl.SuccessGlobal(new GrowlInfo { Message = GetLocalizedString("Growl_Search_Success_Paged", TotalItems, CurrentPage, TotalPages), ShowDateTime = false, StaysOpen = false, WaitTime = 3 });
                _logger.Information("Search successful. {TotalItems} records found. Displaying page {CurrentPage} of {TotalPages}.", TotalItems, CurrentPage, TotalPages);
            }
            else
            {
                // Growl.InfoGlobal(new GrowlInfo { Message = GetLocalizedString("Growl_Search_NoData"), ShowDateTime = false, StaysOpen = false, WaitTime = 3 });
                _logger.Information("No records found matching criteria.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "查询历史记录失败。StartDate={StartDate}, EndDate={EndDate}, BarcodeFilter={BarcodeFilter}, Page={CurrentPage}, PageSize={PageSize}", StartDate, EndDate, BarcodeFilter, CurrentPage, PageSize);
            TotalItems = 0;
            TotalPages = 0;
            CurrentPage = 1; // Reset to page 1 on error
            // Growl.ErrorGlobal(new GrowlInfo { Message = GetLocalizedString("Growl_Search_Error", ex.Message), ShowDateTime = false, StaysOpen = false, WaitTime = 5 });
        }
        finally
        {
            IsLoading = false;
            _logger.Debug("ExecuteSearchCommandAsync: IsLoading set to false in finally block.");
            UpdatePagingCommandsAndInfo();
            RaiseCanExecuteChangedForAllCommands();
            _logger.Debug("ExecuteSearchCommandAsync: Search finished.");
        }
    }

    private bool CanExecutePreviousPageCommand() => CurrentPage > 1 && !IsLoading;
    private async Task ExecutePreviousPageCommandAsync()
    {
        if (CanExecutePreviousPageCommand())
        {
            CurrentPage--;
            await ExecuteSearchCommandAsync();
        }
    }

    private bool CanExecuteNextPageCommand() => CurrentPage < TotalPages && !IsLoading;
    private async Task ExecuteNextPageCommandAsync()
    {
        if (CanExecuteNextPageCommand())
        {
            CurrentPage++;
            await ExecuteSearchCommandAsync();
        }
    }

    private void ExecuteCloseDialogCommand()
    {
        RequestClose.Invoke(new DialogResult(ButtonResult.Cancel));
    }

    public bool CanCloseDialog()
    {
        return !IsLoading;
    }

    public void OnDialogClosed()
    {
        _logger.Debug("历史记录对话框已关闭。");
    }

    public void OnDialogOpened(IDialogParameters parameters)
    {
        var titleParam = parameters.GetValue<string>("title");
        if (!string.IsNullOrEmpty(titleParam))
        {
            Title = titleParam;
        }

        if (parameters.TryGetValue<HistoryViewConfiguration>("customViewConfiguration", out var customConfigFromParams) && customConfigFromParams != null)
        {
            _effectiveColumnSpecs = customConfigFromParams.ColumnSpecs?.Count > 0
                ? customConfigFromParams.ColumnSpecs.OrderBy(c => c.DisplayOrderInGrid).ToList()
                : GetDefaultColumnSpecs().OrderBy(c => c.DisplayOrderInGrid).ToList();
            UpdateColumnVisibilityFromSpecs();
        }
        else if (_configuration != null)
        {
            UpdateColumnVisibilityFromSpecs();
        }

        _logger.Debug("历史记录对话框已打开。");
        _ = ExecuteSearchCommandAsync();
    }

    private bool CanExecuteViewImageCommand(PackageHistoryRecord? package)
    {
        return package != null && !string.IsNullOrEmpty(package.ImagePath) && !IsLoading;
    }

    private void ExecuteViewImageCommand(PackageHistoryRecord? package)
    {
        if (package == null || string.IsNullOrEmpty(package.ImagePath)) return;

        if (File.Exists(package.ImagePath))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = package.ImagePath,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "无法打开图片文件: {ImagePath}", package.ImagePath);
                Growl.ErrorGlobal(new GrowlInfo { Message = GetLocalizedString("Growl_OpenFile_Error", package.ImagePath, ex.Message), ShowDateTime = false, StaysOpen = false, WaitTime = 5 });
            }
        }
        else
        {
            _logger.Warning("尝试查看的图片文件不存在: {ImagePath}", package.ImagePath);
            Growl.WarningGlobal(new GrowlInfo { Message = GetLocalizedString("Growl_ViewImage_FileNotFound", package.ImagePath), ShowDateTime = false, StaysOpen = false, WaitTime = 3 });
        }
    }

    private bool CanExecuteExportToExcelCommand() => TotalItems > 0 && !IsLoading;

    private async Task ExecuteExportToExcelCommand()
    {
        var saveFileDialog = new SaveFileDialog
        {
            Filter = GetLocalizedString("PackageHistory_SaveFile_Filter"),
            FileName = $"{GetLocalizedString("PackageHistory_SaveFile_DefaultName")}_{DateTime.Now:yyyyMMddHHmmss}.xlsx",
            Title = GetLocalizedString("PackageHistory_SaveFile_Title")
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            IsLoading = true;
            var filePath = saveFileDialog.FileName;
            try
            {
                _logger.Information("开始导出所有历史记录到Excel，查询条件 - 开始: {StartDate}, 结束: {EndDate}, 条码: {Barcode}", StartDate, EndDate, BarcodeFilter);
                
                List<PackageHistoryRecord> allPackagesToExport = new List<PackageHistoryRecord>();
                int exportPageSize = 500;
                int currentExportPage = 1;
                (IEnumerable<PackageHistoryRecord> records, int totalCount) exportResult;

                do
                {
                    exportResult = await _packageHistoryDataService.GetPackagesAsync(StartDate, EndDate, BarcodeFilter, currentExportPage, exportPageSize);
                    allPackagesToExport.AddRange(exportResult.records);
                    currentExportPage++;
                } while (allPackagesToExport.Count < exportResult.totalCount && exportResult.records.Any());
                
                _logger.Information("共获取到 {TotalCount} 条记录用于Excel导出。", allPackagesToExport.Count);

                if (_effectiveColumnSpecs != null)
                {
                    var columnsToExport = _effectiveColumnSpecs
                        .Where(cs => cs.IsExported)
                        .OrderBy(cs => cs.DisplayOrderInExcel)
                        .ToList();

                    await Task.Run(() =>
                    {
                        var workbook = new XSSFWorkbook();
                        const string sheetNameKey = "PackageHistory_ExcelSheet_Name";
                        var sheetName = GetLocalizedString(sheetNameKey); 
                        if (string.IsNullOrEmpty(sheetName) || sheetName == sheetNameKey)
                        {
                            sheetName = "历史记录"; 
                        }
                        var sheet = workbook.CreateSheet(sheetName);

                        var headerRow = sheet.CreateRow(0);
                        for (var i = 0; i < columnsToExport.Count; i++)
                        {
                            var columnSpec = columnsToExport[i];
                            var headerText = !string.IsNullOrEmpty(columnSpec.HeaderResourceKey)
                                ? GetLocalizedString(columnSpec.HeaderResourceKey)
                                : columnSpec.PropertyName;
                            headerRow.CreateCell(i).SetCellValue(headerText);
                        }
                    
                        for (var rowIndex = 0; rowIndex < allPackagesToExport.Count; rowIndex++)
                        {
                            var package = allPackagesToExport[rowIndex];
                            var dataRow = sheet.CreateRow(rowIndex + 1);
                            for (var colIndex = 0; colIndex < columnsToExport.Count; colIndex++)
                            {
                                var columnSpec = columnsToExport[colIndex];
                                PropertyInfo? property = null;
                                if(!string.IsNullOrEmpty(columnSpec.PropertyName) && typeof(PackageHistoryRecord).GetProperty(columnSpec.PropertyName) != null)
                                {
                                    property = typeof(PackageHistoryRecord).GetProperty(columnSpec.PropertyName);
                                }
                            
                                object? value = property?.GetValue(package);

                                var cell = dataRow.CreateCell(colIndex);
                                if (value == null)
                                {
                                    continue;
                                }

                                if (property?.PropertyType == typeof(DateTime) || property?.PropertyType == typeof(DateTime?))
                                {
                                    cell.SetCellValue(((DateTime)value).ToString(columnSpec.StringFormat ?? "yyyy-MM-dd HH:mm:ss.fff"));
                                }
                                else if (property?.PropertyType == typeof(double) || property?.PropertyType == typeof(double?))
                                {
                                    cell.SetCellValue((double)value);
                                }
                                else if (property?.PropertyType == typeof(int) || property?.PropertyType == typeof(int?))
                                {
                                    cell.SetCellValue((int)value);
                                }
                                else if (property?.PropertyType == typeof(long) || property?.PropertyType == typeof(long?))
                                {
                                    cell.SetCellValue((long)value);
                                }
                                else if (property?.PropertyType == typeof(decimal) || property?.PropertyType == typeof(decimal?))
                                {
                                    cell.SetCellValue(Convert.ToDouble(value));
                                }
                                else if (property?.PropertyType == typeof(PackageStatus))
                                {
                                    var statusKey = $"PackageStatus_{value}";
                                    var localizedStatus = LocalizeDictionary.Instance.GetLocalizedObject("Common", "Strings", statusKey, LocalizeDictionary.Instance.Culture) as string;
                                    cell.SetCellValue(localizedStatus ?? value.ToString());
                                }
                                else
                                {
                                    cell.SetCellValue(value.ToString());
                                }
                            }
                        }

                        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                        workbook.Write(fileStream);
                    });
                }

                Growl.SuccessGlobal(new GrowlInfo{ Message = GetLocalizedString("Growl_Export_Success", Path.GetFileName(filePath)), ShowDateTime = false, StaysOpen = false, WaitTime = 3 });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "导出历史记录到Excel失败。路径: {FilePath}", filePath);
                Growl.ErrorGlobal(new GrowlInfo{ Message = GetLocalizedString("Growl_Export_Error", ex.Message), ShowDateTime = false, StaysOpen = false, WaitTime = 5 });
            }
            finally
            {
                IsLoading = false;
                RaiseCanExecuteChangedForAllCommands();
            }
        }
    }

    private string GetLocalizedString(string key, params object[] args)
    {
        // 预定义一些常用键的英文回退值
        var englishDefaults = new Dictionary<string, string>
        {
            { "PackageHistory_Paging_NoRecords", "No records" },
            { "PackageHistory_Paging_Info", "Page {0} / {1} (Total {2} items, showing {3} - {4})" }, 
            { "Growl_Search_Success_Paged", "Search successful. {0} records found. Displaying page {1} of {2}." },
            { "Growl_Search_NoData", "No records found matching your criteria." },
            { "Growl_Search_Error", "Failed to search history records: {0}" },
            { "Growl_Export_Success", "Excel export successful: {0}" },
            { "Growl_Export_Error", "Excel export failed: {0}" },
            { "Growl_ViewImage_FileNotFound", "Image file not found: {0}" },
            { "Growl_OpenFile_Error", "Failed to open file {0}: {1}" },
            { "PackageHistory_SaveFile_Filter", "Excel Files (*.xlsx)|*.xlsx" },
            { "PackageHistory_SaveFile_DefaultName", "PackageHistoryLog" },
            { "PackageHistory_SaveFile_Title", "Save Package History" },
            { "PackageHistory_ExcelSheet_Name", "History Log" },
            // 添加更多您在ViewModel中直接使用的键的英文默认值
            { "PackageHistory_Header_Index", "No." },
            { "PackageHistory_Header_Barcode", "Barcode" },
            { "PackageHistory_Header_CreateTime", "Create Time" },
            { "PackageHistory_Header_Status", "Status" },
            { "PackageHistory_Header_Weight", "Weight" },
            { "PackageHistory_Header_ChuteNumber", "Chute No." },
            { "PackageHistory_Header_Length", "Length" },
            { "PackageHistory_Header_Width", "Width" },
            { "PackageHistory_Header_Height", "Height" },
            { "PackageHistory_Header_PalletName", "Pallet Name" },
            { "PackageHistory_Header_PalletWeight", "Pallet Weight" },
            { "PackageHistory_Header_ImageAction", "Image Action" },
            { "PackageHistory_Header_ImagePath", "Image Path" }
        };

        if (englishDefaults.TryGetValue(key, out var englishValue))
        {
            if (args.Length > 0)
            {
                try
                {
                    return string.Format(CultureInfo.InvariantCulture, englishValue, args);
                }
                catch (FormatException ex)
                {
                    _logger.Error(ex, "Fallback English string format failed. Key: '{Key}', Format: '{Format}', Args: {Args}", key, englishValue, args);
                    return englishValue; // 返回未格式化的英文值
                }
            }
            return englishValue;
        }

        // 如果没有预定义的英文回退值，记录警告并返回键或带参数的键
        _logger.Warning("No English default for key '{Key}' in GetLocalizedString. Returning key.", key);
        if (args.Length <= 0) return key;
        try { return key + " (" + string.Join(", ", args.Select(a => a?.ToString() ?? "null")) + ")"; } catch { return key; }
    }
} 
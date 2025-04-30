using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Windows.Input;
using Common.Models.Settings.ChuteRules;
using Common.Services.Settings;
using Common.Services.Ui;
using Microsoft.Win32;
using NPOI.SS.UserModel; // NPOI Core Interface
using NPOI.XSSF.UserModel; // NPOI implementation for .xlsx
// For CellRangeAddress if needed, though not explicitly used here for styling range

// For LINQ validation message access

namespace BenFly.ViewModels.Settings;

internal class ChuteSettingsViewModel : BindableBase
{
    private const string NotificationToken = "SettingWindowGrowl";
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private SegmentCodeRules _configuration = new();

    public ChuteSettingsViewModel(ISettingsService settingsService, INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;

        AddRuleCommand = new DelegateCommand(ExecuteAddRule);
        DeleteRuleCommand = new DelegateCommand<SegmentMatchRule>(ExecuteDeleteRule);
        ImportExcelCommand = new DelegateCommand(ExecuteImportExcel);
        ExportExcelCommand = new DelegateCommand(ExecuteExportExcel);
        SaveCommand = new DelegateCommand(ExecuteSave);

        // 加载配置
        LoadSettings();
    }

    public SegmentCodeRules Configuration
    {
        get => _configuration;
        private set => SetProperty(ref _configuration, value);
    }

    public ICommand AddRuleCommand { get; }
    public ICommand DeleteRuleCommand { get; }
    public ICommand ImportExcelCommand { get; }
    public ICommand ExportExcelCommand { get; }
    internal ICommand SaveCommand { get; }

    private void ExecuteAddRule()
    {
        Configuration.Rules.Add(new SegmentMatchRule());
    }

    private void ExecuteDeleteRule(SegmentMatchRule? rule)
    {
        if (rule != null) Configuration.Rules.Remove(rule);
    }

    private void ExecuteImportExcel()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Excel文件|*.xlsx",
            Title = "选择Excel文件"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            using var fileStream = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read);
            var workbook = new XSSFWorkbook(fileStream); // Use XSSFWorkbook for .xlsx
            var worksheet = workbook.GetSheetAt(0); // Get the first sheet

            if (worksheet.LastRowNum < 1) // Check if there's at least a header row and one data row
            {
                _notificationService.ShowWarningWithToken("Excel文件为空或只有表头", NotificationToken);
                return;
            }

            var rules = new List<SegmentMatchRule>();
            // Start from the second row (index 1) assuming the first row (index 0) is the header
            for (var rowIndex = 1; rowIndex <= worksheet.LastRowNum; rowIndex++)
            {
                var row = worksheet.GetRow(rowIndex);
                if (row == null) continue; // Skip empty rows

                // Use GetCell(index)?.ToString() to safely get cell values as string
                var chuteStr = row.GetCell(0)?.ToString()?.Trim();
                var firstSegment = row.GetCell(1)?.ToString()?.Trim();
                var secondSegment = row.GetCell(2)?.ToString()?.Trim();
                var thirdSegment = row.GetCell(3)?.ToString()?.Trim();

                // Skip rows where all relevant cells are empty
                if (string.IsNullOrWhiteSpace(chuteStr) &&
                    string.IsNullOrWhiteSpace(firstSegment) &&
                    string.IsNullOrWhiteSpace(secondSegment) &&
                    string.IsNullOrWhiteSpace(thirdSegment))
                    continue;

                if (!int.TryParse(chuteStr, out int chuteValue))
                {
                    _notificationService.ShowWarningWithToken($"第{rowIndex + 1}行: 格口号 '{chuteStr}' 必须是有效的数字。", NotificationToken);
                    return;
                }


                var rule = new SegmentMatchRule
                {
                    Chute = chuteValue,
                    FirstSegment = firstSegment ?? string.Empty, // Ensure non-null strings
                    SecondSegment = secondSegment ?? string.Empty,
                    ThirdSegment = thirdSegment ?? string.Empty
                };

                // 验证规则
                var validationContext = new ValidationContext(rule);
                var validationResults = new List<ValidationResult>();
                if (!Validator.TryValidateObject(rule, validationContext, validationResults, true))
                {
                    // Use validationResults.FirstOrDefault() for safer access
                    _notificationService.ShowWarningWithToken($"第{rowIndex + 1}行: {validationResults.FirstOrDefault()?.ErrorMessage ?? "验证错误"}",
                        NotificationToken);
                    return;
                }

                rules.Add(rule);
            }

            Configuration.Rules.Clear();
            foreach (var rule in rules) Configuration.Rules.Add(rule);

            _notificationService.ShowSuccessWithToken("Excel导入成功", NotificationToken);
        }
        catch (Exception ex)
        {
             _notificationService.ShowWarningWithToken($"Excel导入失败: {ex.GetType().Name} - {ex.Message}", NotificationToken);
            // Consider logging the full exception ex here for debugging
        }
    }

     private void ExecuteExportExcel()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Excel文件|*.xlsx",
            Title = "保存Excel文件",
            FileName = "格口规则.xlsx"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var workbook = new XSSFWorkbook(); // Create a new workbook for .xlsx
            var worksheet = workbook.CreateSheet("格口规则");

            // --- Create Styles ---
            // Header Style
            var headerFont = workbook.CreateFont();
            headerFont.IsBold = true;
            var headerStyle = workbook.CreateCellStyle();
            headerStyle.SetFont(headerFont);
            headerStyle.FillForegroundColor = IndexedColors.Grey25Percent.Index; // Light Gray
            headerStyle.FillPattern = FillPattern.SolidForeground;
            headerStyle.BorderBottom = BorderStyle.Thin;
            headerStyle.BorderLeft = BorderStyle.Thin;
            headerStyle.BorderRight = BorderStyle.Thin;
            headerStyle.BorderTop = BorderStyle.Thin;

            // Data Style (basic border)
            var dataStyle = workbook.CreateCellStyle();
            dataStyle.BorderBottom = BorderStyle.Thin;
            dataStyle.BorderLeft = BorderStyle.Thin;
            dataStyle.BorderRight = BorderStyle.Thin;
            dataStyle.BorderTop = BorderStyle.Thin;

            // --- Create Header Row ---
            var headerRow = worksheet.CreateRow(0); // First row (index 0)
            string[] headers = ["格口号", "一段码", "二段码", "三段码"];
            for (var i = 0; i < headers.Length; i++)
            {
                var cell = headerRow.CreateCell(i);
                cell.SetCellValue(headers[i]);
                cell.CellStyle = headerStyle;
            }

            // --- Write Data Rows ---
            for (var i = 0; i < Configuration.Rules.Count; i++)
            {
                var rule = Configuration.Rules[i];
                var dataRow = worksheet.CreateRow(i + 1); // Start data from second row (index 1)

                var cell0 = dataRow.CreateCell(0); cell0.SetCellValue(rule.Chute); cell0.CellStyle = dataStyle;
                var cell1 = dataRow.CreateCell(1); cell1.SetCellValue(rule.FirstSegment); cell1.CellStyle = dataStyle;
                var cell2 = dataRow.CreateCell(2); cell2.SetCellValue(rule.SecondSegment); cell2.CellStyle = dataStyle;
                var cell3 = dataRow.CreateCell(3); cell3.SetCellValue(rule.ThirdSegment); cell3.CellStyle = dataStyle;
            }

            // --- Auto Adjust Column Widths ---
            for (var i = 0; i < headers.Length; i++)
            {
                worksheet.AutoSizeColumn(i);
            }

            // --- Save File ---
            using var fileStream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write);
            workbook.Write(fileStream);

            _notificationService.ShowSuccessWithToken("Excel导出成功", NotificationToken);
        }
        catch (Exception ex)
        {
            _notificationService.ShowWarningWithToken($"Excel导出失败: {ex.GetType().Name} - {ex.Message}", NotificationToken);
        }
    }


    private void ExecuteSave()
    {
        // 验证异常格口
        var validationContext = new ValidationContext(Configuration);
        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(Configuration, validationContext, validationResults, true))
        {
             _notificationService.ShowWarningWithToken(validationResults.FirstOrDefault()?.ErrorMessage ?? "配置验证失败", NotificationToken);
            return;
        }

        // 验证规则列表
        foreach (var rule in Configuration.Rules)
        {
            validationContext = new ValidationContext(rule);
            validationResults = []; // Reset for each rule
            if (Validator.TryValidateObject(rule, validationContext, validationResults, true)) continue;

            _notificationService.ShowWarningWithToken(validationResults.FirstOrDefault()?.ErrorMessage ?? $"规则 '{rule.Chute}' 验证失败", NotificationToken);
            return;
        }

        _settingsService.SaveSettings(Configuration);
         _notificationService.ShowSuccessWithToken("格口规则已保存", NotificationToken); // Add save confirmation
    }

    private void LoadSettings()
    {
        Configuration = _settingsService.LoadSettings<SegmentCodeRules>();
    }
}
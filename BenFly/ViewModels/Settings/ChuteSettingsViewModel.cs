using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.IO;
using System.Windows.Input;
using Common.Models.Settings.ChuteRules;
using Common.Services.Settings;
using Common.Services.Ui;
using Microsoft.Win32;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using Prism.Commands;
using Prism.Mvvm;

namespace Presentation_BenFly.ViewModels.Settings;

public class ChuteSettingsViewModel : BindableBase
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
        DeleteRuleCommand = new DelegateCommand<ChuteRule>(ExecuteDeleteRule);
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
    public ICommand SaveCommand { get; }

    private void ExecuteAddRule()
    {
        Configuration.Rules.Add(new ChuteRule());
    }

    private void ExecuteDeleteRule(ChuteRule? rule)
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
            // 设置EPPlus许可证
#pragma warning disable CS0618
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
#pragma warning restore CS0618
            using var package = new ExcelPackage(new FileInfo(dialog.FileName));
            var worksheet = package.Workbook.Worksheets[0];

            if (worksheet.Dimension?.End == null)
            {
                _notificationService.ShowWarningWithToken("Excel文件为空", NotificationToken);
                return;
            }

            var rules = new List<ChuteRule>();
            for (var row = 2; row <= worksheet.Dimension.End.Row; row++)
            {
                var chute = worksheet.Cells[row, 1].Text;
                var firstSegment = worksheet.Cells[row, 2].Text;
                var secondSegment = worksheet.Cells[row, 3].Text;
                var thirdSegment = worksheet.Cells[row, 4].Text;

                if (string.IsNullOrWhiteSpace(chute) &&
                    string.IsNullOrWhiteSpace(firstSegment) &&
                    string.IsNullOrWhiteSpace(secondSegment) &&
                    string.IsNullOrWhiteSpace(thirdSegment))
                    continue;

                var rule = new ChuteRule
                {
                    Chute = Convert.ToInt32(chute),
                    FirstSegment = firstSegment,
                    SecondSegment = secondSegment,
                    ThirdSegment = thirdSegment
                };

                // 验证规则
                var validationContext = new ValidationContext(rule);
                var validationResults = new List<ValidationResult>();
                if (!Validator.TryValidateObject(rule, validationContext, validationResults, true))
                {
                    _notificationService.ShowWarningWithToken($"第{row}行: {validationResults[0].ErrorMessage}",
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
            _notificationService.ShowWarningWithToken($"Excel导入失败: {ex.Message}", NotificationToken);
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
            // 设置EPPlus许可证
#pragma warning disable CS0618
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
#pragma warning restore CS0618

            using var package = new ExcelPackage();
            var worksheet = package.Workbook.Worksheets.Add("格口规则");

            // 设置表头
            worksheet.Cells[1, 1].Value = "格口号";
            worksheet.Cells[1, 2].Value = "一段码";
            worksheet.Cells[1, 3].Value = "二段码";
            worksheet.Cells[1, 4].Value = "三段码";

            // 设置表头样式
            using (var range = worksheet.Cells[1, 1, 1, 4])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(Color.LightGray);
                range.Style.Border.BorderAround(ExcelBorderStyle.Thin);
            }

            // 写入数据
            for (var i = 0; i < Configuration.Rules.Count; i++)
            {
                var rule = Configuration.Rules[i];
                var row = i + 2;

                worksheet.Cells[row, 1].Value = rule.Chute;
                worksheet.Cells[row, 2].Value = rule.FirstSegment;
                worksheet.Cells[row, 3].Value = rule.SecondSegment;
                worksheet.Cells[row, 4].Value = rule.ThirdSegment;

                // 设置数据单元格样式
                using var range = worksheet.Cells[row, 1, row, 4];
                range.Style.Border.BorderAround(ExcelBorderStyle.Thin);
            }

            // 自动调整列宽
            worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();

            // 保存文件
            package.SaveAs(new FileInfo(dialog.FileName));
            _notificationService.ShowSuccessWithToken("Excel导出成功", NotificationToken);
        }
        catch (Exception ex)
        {
            _notificationService.ShowWarningWithToken($"Excel导出失败: {ex.Message}", NotificationToken);
        }
    }

    private void ExecuteSave()
    {
        // 验证异常格口
        var validationContext = new ValidationContext(Configuration);
        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(Configuration, validationContext, validationResults, true))
        {
            _notificationService.ShowWarningWithToken(validationResults[0].ErrorMessage, NotificationToken);
            return;
        }

        // 验证规则列表
        foreach (var rule in Configuration.Rules)
        {
            validationContext = new ValidationContext(rule);
            validationResults = [];
            if (Validator.TryValidateObject(rule, validationContext, validationResults, true)) continue;
            _notificationService.ShowWarningWithToken(validationResults[0].ErrorMessage, NotificationToken);
            return;
        }

        _settingsService.SaveSettings(Configuration);
    }

    private void LoadSettings()
    {
        Configuration = _settingsService.LoadSettings<SegmentCodeRules>();
    }
}
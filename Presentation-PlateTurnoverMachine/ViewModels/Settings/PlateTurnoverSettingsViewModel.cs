using System.IO;
using CommonLibrary.Services;
using Microsoft.Win32;
using OfficeOpenXml;
using Presentation_CommonLibrary.Services;
using Presentation_PlateTurnoverMachine.Models;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using Wpf.Ui.Controls;

namespace Presentation_PlateTurnoverMachine.ViewModels.Settings;

public class PlateTurnoverSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;
    private readonly INotificationService _notificationService;
    private string _infoTitle = string.Empty;
    private string _infoMessage = string.Empty;
    private bool _isInfoBarOpen;
    private InfoBarSeverity _infoSeverity;
    private PlateTurnoverSettings _settings = new();

    public PlateTurnoverSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;

        // 在构造函数中初始化命令
        AddItemCommand = new DelegateCommand(AddItem);
        RemoveItemCommand = new DelegateCommand<PlateTurnoverItem>(RemoveItem);
        ImportFromExcelCommand = new DelegateCommand(ExecuteImportFromExcel);
        ExportToExcelCommand = new DelegateCommand(ExecuteExportToExcel);
        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);

        // 加载配置
        LoadSettings();
    }

    #region Properties

    public PlateTurnoverSettings Settings
    {
        get => _settings;
        private set => SetProperty(ref _settings, value);
    }

    public string InfoTitle
    {
        get => _infoTitle;
        set => SetProperty(ref _infoTitle, value);
    }

    public string InfoMessage
    {
        get => _infoMessage;
        set => SetProperty(ref _infoMessage, value);
    }

    public bool IsInfoBarOpen
    {
        get => _isInfoBarOpen;
        set => SetProperty(ref _isInfoBarOpen, value);
    }

    public InfoBarSeverity InfoSeverity
    {
        get => _infoSeverity;
        set => SetProperty(ref _infoSeverity, value);
    }

    #endregion

    #region Commands

    public DelegateCommand AddItemCommand { get; private set; }
    public DelegateCommand<PlateTurnoverItem> RemoveItemCommand { get; private set; }
    public DelegateCommand ImportFromExcelCommand { get; private set; }
    public DelegateCommand ExportToExcelCommand { get; private set; }
    public DelegateCommand SaveConfigurationCommand { get; private set; }

    #endregion

    #region Private Methods

    private void LoadSettings()
    {
        try
        {
            Settings = _settingsService.LoadConfiguration<PlateTurnoverSettings>();
            ShowInfo("配置加载成功", "已从配置文件加载翻板机设置", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载翻板机配置失败");
            ShowInfo("配置加载失败", ex.Message, InfoBarSeverity.Error);
            Settings.Items = [];
        }
    }

    private void AddItem()
    {
        var newItem = new PlateTurnoverItem
        {
            // Index = Settings.Items.Count + 1  // 直接设置新项的索引
        };
        Settings.Items.Add(newItem);
        ShowInfo("添加成功", "已添加新的翻板机配置项", InfoBarSeverity.Success);
    }

    private void RemoveItem(PlateTurnoverItem item)
    {
        var index = Settings.Items.IndexOf(item);
        if (index == -1) return;

        Settings.Items.Remove(item);
        
        // // 更新后续项的索引
        // for (int i = index; i < Settings.Items.Count; i++)
        // {
        //     Settings.Items[i].Index = i + 1;
        // }
        
        ShowInfo("删除成功", "已删除选中的翻板机配置项", InfoBarSeverity.Success);
    }

    private void ExecuteImportFromExcel()
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Excel文件|*.xlsx",
                Title = "选择要导入的Excel文件"
            };

            if (dialog.ShowDialog() != true) return;

            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using var package = new ExcelPackage(new FileInfo(dialog.FileName));
            var worksheet = package.Workbook.Worksheets[0];

            Settings.Items.Clear();
            var row = 2; // 从第二行开始读取数据
            while (worksheet.Cells[row, 1].Value != null)
            {
                var item = new PlateTurnoverItem
                {
                    TcpAddress = GetCellValue(worksheet.Cells[row, 2]),
                    IoPoint = GetCellValue(worksheet.Cells[row, 3]),
                    // MappedPort = GetCellValue(worksheet.Cells[row, 4]),
                    Distance = double.Parse(GetCellValue(worksheet.Cells[row, 5])),
                    DelayFactor = double.Parse(GetCellValue(worksheet.Cells[row, 6])),
                    MagnetTime = int.Parse(GetCellValue(worksheet.Cells[row, 7]))
                };
                Settings.Items.Add(item);
                row++;
            }
            ShowInfo("导入成功", $"已从Excel导入 {Settings.Items.Count} 条配置", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导入Excel失败");
            ShowInfo("导入失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ExecuteExportToExcel()
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Excel文件|*.xlsx",
                Title = "选择导出位置",
                FileName = $"翻板机配置_{DateTime.Now:yyyyMMddHHmmss}.xlsx"
            };

            if (dialog.ShowDialog() != true) return;

            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using var package = new ExcelPackage();
            var worksheet = package.Workbook.Worksheets.Add("翻板机配置");

            // 创建表头
            var headers = new[] { "序号", "TCP模块", "IO点位", "映射格口", "距离当前点位位置", "分拣延迟系数(0-1)", "磁铁吸合时间(ms)" };
            for (var i = 0; i < headers.Length; i++)
            {
                worksheet.Cells[1, i + 1].Value = headers[i];
            }

            // 写入数据
            for (var i = 0; i < Settings.Items.Count; i++)
            {
                var item = Settings.Items[i];
                var row = i + 2;

                // worksheet.Cells[row, 1].Value = item.Index;
                worksheet.Cells[row, 2].Value = item.TcpAddress;
                worksheet.Cells[row, 3].Value = item.IoPoint;
                worksheet.Cells[row, 4].Value = item.MappingChute;
                worksheet.Cells[row, 5].Value = item.Distance;
                worksheet.Cells[row, 6].Value = item.DelayFactor;
                worksheet.Cells[row, 7].Value = item.MagnetTime;
            }

            // 保存文件
            package.SaveAs(new FileInfo(dialog.FileName));

            ShowInfo("导出成功", $"已导出 {Settings.Items.Count} 条配置到Excel", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出Excel失败");
            ShowInfo("导出失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ExecuteSaveConfiguration()
    {
        try
        {
            _settingsService.SaveConfiguration(Settings);
            ShowInfo("保存成功", "翻板机配置已保存", InfoBarSeverity.Success);
            _notificationService.ShowSuccessWithToken("翻板机配置已保存", "SettingWindowGrowl");
            
            // 更新光电设备配置
            try
            {
                Log.Information("已更新光电设备配置");
                _notificationService.ShowSuccessWithToken("已更新光电设备配置", "SettingWindowGrowl");
            }
            catch (Exception updateEx)
            {
                Log.Error(updateEx, "更新光电设备配置失败");
                _notificationService.ShowErrorWithToken("更新光电设备配置失败: " + updateEx.Message, "SettingWindowGrowl");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存翻板机配置失败");
            ShowInfo("保存失败", ex.Message, InfoBarSeverity.Error);
            _notificationService.ShowErrorWithToken("保存翻板机配置失败", "SettingWindowGrowl");
        }
    }

    private static string GetCellValue(ExcelRange cell)
    {
        return cell.Value?.ToString() ?? string.Empty;
    }

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        InfoTitle = title;
        InfoMessage = message;
        InfoSeverity = severity;
        IsInfoBarOpen = true;
    }

    #endregion
}
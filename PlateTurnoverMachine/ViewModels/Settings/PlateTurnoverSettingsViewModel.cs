using System.IO;
using System.Net;
using System.Collections.ObjectModel;
using Common.Services.Settings;
using Common.Services.Ui;
using Microsoft.Win32;
using OfficeOpenXml;
using PlateTurnoverMachine.Models;
using PlateTurnoverMachine.Models.Settings;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;
using Wpf.Ui.Controls;

namespace PlateTurnoverMachine.ViewModels.Settings;

internal class PlateTurnoverSettingsViewModel : BindableBase
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private string _infoMessage = string.Empty;
    private InfoBarSeverity _infoSeverity;
    private string _infoTitle = string.Empty;
    private bool _isInfoBarOpen;
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
    public DelegateCommand SaveConfigurationCommand { get; set; }

    #endregion

    #region Private Methods

    private void LoadSettings()
    {
        try
        {
            Settings = _settingsService.LoadSettings<PlateTurnoverSettings>();

            // 如果配置为空，则根据格口数量初始化配置
            if (Settings.Items.Count == 0)
            {
                InitializeSettingsByChuteCount();
            }

            ShowInfo("配置加载成功", "已从配置文件加载翻板机设置", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载翻板机配置失败");
            ShowInfo("配置加载失败", ex.Message, InfoBarSeverity.Error);
            Settings.Items = [];
        }
    }

    private void InitializeSettingsByChuteCount()
    {
        try
        {
            // 获取格口设置
            var chuteSettings = _settingsService.LoadSettings<ChuteSettings>();
            if (chuteSettings.ChuteCount <= 0)
            {
                Log.Warning("格口数量未设置，无法初始化翻板机配置");
                return;
            }

            var chuteCount = chuteSettings.ChuteCount;
            const int itemsPerIp = 8; // 每个IP地址对应8个格口
            var ipCount = (int)Math.Ceiling(chuteCount / (double)itemsPerIp);

            // 从192.168.0.100开始
            var baseIpParts = new byte[] { 192, 168, 0, 100 };
            var items = new ObservableCollection<PlateTurnoverItem>();

            for (var i = 0; i < ipCount; i++)
            {
                // 计算当前IP地址
                var currentIpParts = (byte[])baseIpParts.Clone();
                currentIpParts[3] = (byte)(100 + i);
                var ipAddress = new IPAddress(currentIpParts);

                // 计算当前IP对应的格口范围
                var startChute = i * itemsPerIp + 1;
                var endChute = Math.Min(startChute + itemsPerIp - 1, chuteCount);

                for (var chute = startChute; chute <= endChute; chute++)
                {
                    var outNumber = chute - startChute + 1;
                    items.Add(new PlateTurnoverItem
                    {
                        TcpAddress = ipAddress.ToString(),
                        IoPoint = $"out{outNumber}",
                        MappingChute = chute,
                        Distance = 0,
                        DelayFactor = 1,
                        MagnetTime = 100
                    });
                }
            }

            Settings.Items = items;
            Log.Information($"已根据格口数量({chuteCount})初始化{items.Count}条翻板机配置");
            ShowInfo("初始化成功", $"已根据格口数量({chuteCount})初始化{items.Count}条配置", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化翻板机配置失败");
            ShowInfo("初始化失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void AddItem()
    {
        var newItem = new PlateTurnoverItem();
        Settings.Items.Add(newItem);
        ShowInfo("添加成功", "已添加新的翻板机配置项", InfoBarSeverity.Success);
    }

    private void RemoveItem(PlateTurnoverItem item)
    {
        var index = Settings.Items.IndexOf(item);
        if (index == -1) return;

        Settings.Items.Remove(item);
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
                    MappingChute = int.Parse(GetCellValue(worksheet.Cells[row, 4])),
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

                worksheet.Cells[row, 1].Value = i + 1;
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
            _settingsService.SaveSettings(Settings);
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
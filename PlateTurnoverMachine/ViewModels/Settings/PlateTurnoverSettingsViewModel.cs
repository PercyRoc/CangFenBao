using System.IO;
using System.Net;
using System.Collections.ObjectModel;
using Common.Services.Settings;
using Common.Services.Ui;
using Microsoft.Win32;
using OfficeOpenXml;
using PlateTurnoverMachine.Models;
using Prism.Commands;
using Prism.Mvvm;
using Serilog;

namespace PlateTurnoverMachine.ViewModels.Settings;

internal class PlateTurnoverSettingsViewModel : BindableBase, IDisposable
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private PlateTurnoverSettings _settings = new();
    private bool _isAutoSaveEnabled = true;

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

        // 订阅配置变更事件以实现自动保存
        Settings.SettingsChanged += OnSettingsChanged;

        // 订阅格口总数变更事件
        Settings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(PlateTurnoverSettings.ChuteCount)) return;
            InitializeSettingsByChuteCount();
        };
    }

    #region Properties

    public PlateTurnoverSettings Settings
    {
        get => _settings;
        private set => SetProperty(ref _settings, value);
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
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载翻板机配置失败");
            Settings.Items = [];
        }
    }

    private void InitializeSettingsByChuteCount()
    {
        try
        {
            // 获取格口设置
            var settings = _settingsService.LoadSettings<PlateTurnoverSettings>();
            if (settings.ChuteCount <= 0)
            {
                Log.Warning("格口数量未设置，无法初始化翻板机配置");
                return;
            }

            var chuteCount = settings.ChuteCount;
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
                    var item = new PlateTurnoverItem
                    {
                        Index = (items.Count + 1).ToString(),
                        TcpAddress = ipAddress.ToString(),
                        IoPoint = $"out{outNumber}",
                        MappingChute = chute,
                        Distance = 0,
                        DelayFactor = 1,
                        MagnetTime = 100
                    };
                    items.Add(item);
                }
            }

            Settings.Items = items;
            Log.Information("已根据格口数量({ChuteCount})初始化{ItemsCount}条翻板机配置", chuteCount, items.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化翻板机配置失败");
        }
    }

    private void AddItem()
    {
        var newItem = new PlateTurnoverItem();
        Settings.Items.Add(newItem);
    }

    private void RemoveItem(PlateTurnoverItem item)
    {
        var index = Settings.Items.IndexOf(item);
        if (index == -1) return;

        Settings.Items.Remove(item);
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

            // 导入时关闭自动保存，避免频繁触发保存
            _isAutoSaveEnabled = false;

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

            // 恢复自动保存并手动保存一次
            _isAutoSaveEnabled = true;
            SaveSettings();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导入Excel失败");
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
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出Excel失败");
        }
    }

    private void ExecuteSaveConfiguration()
    {
        SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            _settingsService.SaveSettings(Settings);
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
            _notificationService.ShowErrorWithToken("保存翻板机配置失败", "SettingWindowGrowl");
        }
    }

    private static string GetCellValue(ExcelRange cell)
    {
        return cell.Value?.ToString() ?? string.Empty;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // 属性变更时自动保存
        if (!_isAutoSaveEnabled) return;
        try
        {
            _settingsService.SaveSettings(Settings);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "自动保存翻板机配置失败");
        }
    }

    // 释放资源，取消事件订阅
    public void Dispose()
    {
        Settings.SettingsChanged -= OnSettingsChanged;
    }

    #endregion
}
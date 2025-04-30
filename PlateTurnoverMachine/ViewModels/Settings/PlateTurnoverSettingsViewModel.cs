using System.IO;
using System.Net;
using System.Collections.ObjectModel;
using Common.Services.Settings;
using Common.Services.Ui;
using Microsoft.Win32;
using Serilog;
using DongtaiFlippingBoardMachine.Models;
using System.Globalization;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace DongtaiFlippingBoardMachine.ViewModels.Settings;

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
            // 初始化为空集合，避免 NullReferenceException
            Settings = new PlateTurnoverSettings { Items = new ObservableCollection<PlateTurnoverItem>() };
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
                        Index = (items.Count + 1).ToString(), // Index 似乎未使用，暂时保留
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
        // 可以设置默认值
        Settings.Items.Add(newItem);
    }

    private void RemoveItem(PlateTurnoverItem item)
    {
        Settings.Items.Remove(item);
    }

    // --- NPOI Import ---
    private void ExecuteImportFromExcel()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Excel 文件|*.xlsx",
            Title = "选择要导入的 Excel 文件"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            // 导入时关闭自动保存
            _isAutoSaveEnabled = false;

            using var stream = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read);
            var workbook = new XSSFWorkbook(stream);
            var sheet = workbook.GetSheetAt(0); // 获取第一个工作表

            Settings.Items.Clear();
            var importedItems = new List<PlateTurnoverItem>();

            // 从第二行开始读取数据 (索引从0开始，所以是 i = 1)
            for (var i = 1; i <= sheet.LastRowNum; i++)
            {
                var row = sheet.GetRow(i);
                if (row == null) continue; // 跳过空行

                try
                {
                    var item = new PlateTurnoverItem
                    {
                        // 第1列是序号，我们重新生成，不读取
                        // Index = GetSafeStringCellValue(row.GetCell(0)), // 序号列 (A)
                        TcpAddress = GetSafeStringCellValue(row.GetCell(1)), // TCP模块 (B)
                        IoPoint = GetSafeStringCellValue(row.GetCell(2)),    // IO点位 (C)
                        MappingChute = (int)GetSafeNumericCellValue(row.GetCell(3)), // 映射格口 (D)
                        Distance = GetSafeNumericCellValue(row.GetCell(4)),      // 距离 (E)
                        DelayFactor = GetSafeNumericCellValue(row.GetCell(5)),   // 延迟系数 (F)
                        MagnetTime = (int)GetSafeNumericCellValue(row.GetCell(6)),
                        Index = (importedItems.Count + 1).ToString() // 重新生成序号
                        // 磁铁时间 (G)
                    };
                    importedItems.Add(item);
                }
                catch (Exception cellEx)
                {
                    Log.Error(cellEx, "处理 Excel 第 {RowIndex} 行数据时出错", i + 1);
                    _notificationService.ShowError($"导入 Excel 第 {i + 1} 行时出错: {cellEx.Message}");
                    // 根据需要决定是否继续导入或中断
                    // return;
                }
            }
            Settings.Items = new ObservableCollection<PlateTurnoverItem>(importedItems);
            _notificationService.ShowSuccess($"成功导入 {Settings.Items.Count} 条数据");
            Log.Information("从 Excel 成功导入 {Count} 条翻板机配置", Settings.Items.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导入 Excel 失败: {FilePath}", dialog.FileName);
            _notificationService.ShowError($"导入 Excel 文件失败: {ex.Message}");
        }
        finally
        {
            // 恢复自动保存并手动保存一次
            _isAutoSaveEnabled = true;
            if (Settings.Items.Any()) // 仅当导入成功时保存
            {
                 SaveSettings();
            }
        }
    }

    // --- NPOI Export ---
    private void ExecuteExportToExcel()
    {
         var dialog = new SaveFileDialog
         {
             Filter = "Excel 文件|*.xlsx",
             Title = "选择导出位置",
             FileName = $"翻板机配置_{DateTime.Now:yyyyMMddHHmmss}.xlsx"
         };

         if (dialog.ShowDialog() != true) return;

         try
         {
             var workbook = new XSSFWorkbook();
             var sheet = workbook.CreateSheet("翻板机配置");

             // 创建表头
             var headerRow = sheet.CreateRow(0);
             var headers = new[] { "序号", "TCP模块", "IO点位", "映射格口", "距离当前点位位置", "分拣延迟系数(0-1)", "磁铁吸合时间(ms)" };
             for (var i = 0; i < headers.Length; i++)
             {
                 var cell = headerRow.CreateCell(i);
                 cell.SetCellValue(headers[i]);
                 // 可以设置表头样式
             }

             // 写入数据 (数据行从索引1开始)
             for (var i = 0; i < Settings.Items.Count; i++)
             {
                 var item = Settings.Items[i];
                 var dataRow = sheet.CreateRow(i + 1);

                 dataRow.CreateCell(0).SetCellValue(i + 1); // 序号
                 dataRow.CreateCell(1).SetCellValue(item.TcpAddress);
                 dataRow.CreateCell(2).SetCellValue(item.IoPoint);
                 dataRow.CreateCell(3).SetCellValue(item.MappingChute);
                 dataRow.CreateCell(4).SetCellValue(item.Distance);
                 dataRow.CreateCell(5).SetCellValue(item.DelayFactor);
                 dataRow.CreateCell(6).SetCellValue(item.MagnetTime);
             }

             // 自动调整列宽 (可选, 可能影响性能)
             // for (int i = 0; i < headers.Length; i++) {
             //     sheet.AutoSizeColumn(i);
             // }

             // 保存文件
             using var stream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write);
             workbook.Write(stream);

             _notificationService.ShowSuccess("配置已成功导出到 Excel");
             Log.Information("翻板机配置已导出到: {FilePath}", dialog.FileName);
         }
         catch (Exception ex)
         {
             Log.Error(ex, "导出 Excel 失败");
             _notificationService.ShowError($"导出 Excel 文件失败: {ex.Message}");
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

            // 更新光电设备配置 (这段逻辑保持不变)
            try
            {
                Log.Information("触发光电设备配置更新 (具体逻辑待确认)");
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

    // --- NPOI Helper Methods ---
    private static string GetSafeStringCellValue(ICell? cell)
    {
        if (cell == null) return string.Empty;

        return cell.CellType switch
        {
            CellType.String => cell.StringCellValue,
            CellType.Numeric => cell.NumericCellValue.ToString(CultureInfo.InvariantCulture), // 数字也转为字符串
            CellType.Boolean => cell.BooleanCellValue.ToString(),
            CellType.Formula => GetFormulaCellValue(cell), // 处理公式
            _ => cell.ToString() ?? string.Empty, // 其他类型尝试ToString
        };
    }

     private static double GetSafeNumericCellValue(ICell? cell)
     {
         if (cell == null) return 0;

         return cell.CellType switch
         {
             CellType.Numeric => cell.NumericCellValue,
             CellType.String => double.TryParse(cell.StringCellValue, out var value) ? value : 0, // 尝试转换字符串
             CellType.Formula => GetFormulaNumericValue(cell), // 处理公式
             _ => 0 // 其他类型返回0
         };
     }

    // 辅助方法：获取公式单元格的计算结果（字符串）
    private static string GetFormulaCellValue(ICell cell)
    {
        try
        {
            // 尝试获取公式计算后的字符串值
            return cell.StringCellValue;
        }
        catch
        {
            try
            {
                // 如果字符串失败，尝试获取数字值并转为字符串
                return cell.NumericCellValue.ToString(CultureInfo.InvariantCulture);
            }
            catch
            {
                // 都失败则返回空
                return string.Empty;
            }
        }
    }
        // 辅助方法：获取公式单元格的计算结果（数字）
    private static double GetFormulaNumericValue(ICell cell)
    {
        try
        {
            // 尝试获取公式计算后的数字值
             return cell.NumericCellValue;
        }
        catch
        {
             try
             {
                 // 如果数字失败，尝试获取字符串并解析
                 return double.TryParse(cell.StringCellValue, out var value) ? value : 0;
             }
             catch
             {
                 // 都失败则返回0
                 return 0;
             }
        }
    }


    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // 属性变更时自动保存
        if (!_isAutoSaveEnabled) return;
        // 考虑增加防抖或延迟保存，避免过于频繁的IO操作
        SaveSettings();
    }

    // 释放资源，取消事件订阅
    public void Dispose()
    {
        Settings.PropertyChanged -= Settings_PropertyChanged; // 取消订阅属性变更
        Settings.SettingsChanged -= OnSettingsChanged; // 取消订阅集合内容变更
        GC.SuppressFinalize(this); // 通知GC不需要调用Finalize
        Log.Debug("PlateTurnoverSettingsViewModel disposed.");
    }

     // 处理 Settings 对象本身被替换的情况，重新订阅事件
     private void Settings_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
     {
         if (e.PropertyName == nameof(PlateTurnoverSettings.ChuteCount))
         {
             InitializeSettingsByChuteCount();
         }
     }


    #endregion
}
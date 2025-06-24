using System.IO;
using System.Net;
using System.Collections.ObjectModel;
using Common.Services.Settings;
using Common.Services.Ui;
using Microsoft.Win32;
using Serilog;
using DongtaiFlippingBoardMachine.Models;
using System.Globalization;
using DongtaiFlippingBoardMachine.Events;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using Prism.Events;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;

namespace DongtaiFlippingBoardMachine.ViewModels.Settings;

internal class PlateTurnoverSettingsViewModel : BindableBase, IDisposable
{
    private readonly INotificationService _notificationService;
    private readonly ISettingsService _settingsService;
    private readonly IEventAggregator _eventAggregator;
    private PlateTurnoverSettings _settings = new();
    private CancellationTokenSource? _autoSaveCts;

    public PlateTurnoverSettingsViewModel(
        ISettingsService settingsService,
        INotificationService notificationService,
        IEventAggregator eventAggregator)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        _eventAggregator = eventAggregator;

        // 在构造函数中初始化命令
        AddItemCommand = new DelegateCommand(AddItem);
        RemoveItemCommand = new DelegateCommand<PlateTurnoverItem>(RemoveItem);
        ImportFromExcelCommand = new DelegateCommand(async () => await ExecuteImportFromExcelAsync());
        ExportToExcelCommand = new DelegateCommand(ExecuteExportToExcel);
        SaveConfigurationCommand = new DelegateCommand(async () => await SaveSettingsAsync());
        InitializeByChuteCountCommand = new DelegateCommand(async () => await InitializeSettingsByChuteCountAsync());

        // 加载配置
        LoadSettings();

        // 订阅配置对象本身的属性变更
        _settings.PropertyChanged += Settings_PropertyChanged;
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
    public DelegateCommand InitializeByChuteCountCommand { get; set; }

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
        
        // 确保为加载的配置项订阅深度监听
        SubscribeToItemChanges(Settings.Items);
    }

    private async Task InitializeSettingsByChuteCountAsync()
    {
        try
        {
            // 预检查，以便在无法生成时提供即时反馈
            if (_settingsService.LoadSettings<PlateTurnoverSettings>().ChuteCount <= 0)
            {
                Log.Warning("格口数量未设置，无法初始化翻板机配置");
                _notificationService.ShowWarning("请先设置一个大于0的格口总数。");
                return;
            }

            // 在后台线程执行初始化操作
            var newItems = await Task.Run(GenerateItemsByChuteCount);

            if (newItems != null && newItems.Any())
            {
                UnsubscribeFromItemChanges(Settings.Items);
                // 在 UI 线程更新集合
                Settings.Items = newItems;
                SubscribeToItemChanges(Settings.Items);
                Log.Information("已根据格口数量({ChuteCount})初始化{ItemsCount}条翻板机配置", Settings.ChuteCount, newItems.Count);
                _notificationService.ShowSuccess($"已生成 {newItems.Count} 条配置，请检查后手动保存。");
            }
            else
            {
                Log.Warning("未能生成任何配置项。");
                _notificationService.ShowWarning("未能生成配置，请检查格口数量设置。");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化翻板机配置失败");
            _notificationService.ShowError("初始化配置时发生错误。");
        }
    }

    private void InitializeSettingsByChuteCount()
    {
        try
        {
            var newItems = GenerateItemsByChuteCount();
            if (newItems != null)
            {
                UnsubscribeFromItemChanges(Settings.Items);
                Settings.Items = newItems;
                SubscribeToItemChanges(Settings.Items);
                Log.Information("已根据格口数量({ChuteCount})初始化{ItemsCount}条翻板机配置", Settings.ChuteCount, newItems.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化翻板机配置失败");
        }
    }

    private ObservableCollection<PlateTurnoverItem>? GenerateItemsByChuteCount()
    {
        var settings = _settingsService.LoadSettings<PlateTurnoverSettings>();
        var chuteCount = settings.ChuteCount;
        var items = new ObservableCollection<PlateTurnoverItem>();

        if (chuteCount == 94) // 用户的特殊配置
        {
            Log.Information("检测到94格口，应用特殊配置生成规则：12个控制盒，首尾各7个IO点，中间各8个。");
            const int totalBoxes = 12;
            var baseIpParts = new byte[] { 192, 168, 0, 100 };
            int chuteCursor = 1;

            for (var boxIndex = 0; boxIndex < totalBoxes; boxIndex++)
            {
                var chutesInThisBox = boxIndex == 0 || boxIndex == totalBoxes - 1 ? 7 : 8;

                var currentIpParts = (byte[])baseIpParts.Clone();
                currentIpParts[3] = (byte)(100 + boxIndex);
                var ipAddress = new IPAddress(currentIpParts).ToString();

                for (var i = 0; i < chutesInThisBox; i++)
                {
                    if (chuteCursor > chuteCount) break;

                    var item = new PlateTurnoverItem
                    {
                        Index = (items.Count + 1).ToString(),
                        TcpAddress = ipAddress,
                        IoPoint = $"out{i + 1}",
                        MappingChute = chuteCursor,
                        Distance = 0,
                        DelayFactor = 1,
                        MagnetTime = 300
                    };
                    items.Add(item);
                    chuteCursor++;
                }
            }
        }
        else // 回退到原始通用逻辑
        {
            if (chuteCount <= 0)
            {
                Log.Warning("格口数量未设置，无法初始化翻板机配置");
                // 在同步方法中不显示通知，避免不必要的UI交互
                return null;
            }

            const int itemsPerIp = 8;
            var ipCount = (int)Math.Ceiling(chuteCount / (double)itemsPerIp);
            var baseIpParts = new byte[] { 192, 168, 0, 100 };

            for (var i = 0; i < ipCount; i++)
            {
                var currentIpParts = (byte[])baseIpParts.Clone();
                currentIpParts[3] = (byte)(100 + i);
                var ipAddress = new IPAddress(currentIpParts);

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
                        MagnetTime = 300
                    };
                    items.Add(item);
                }
            }
        }

        return items.Any() ? items : null;
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
    private async Task ExecuteImportFromExcelAsync()
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
                    return;
                }
            }

            UnsubscribeFromItemChanges(Settings.Items);
            Settings.Items = new ObservableCollection<PlateTurnoverItem>(importedItems);
            SubscribeToItemChanges(Settings.Items);
            _notificationService.ShowSuccess($"成功导入 {Settings.Items.Count} 条数据");
            Log.Information("从 Excel 成功导入 {Count} 条翻板机配置", Settings.Items.Count);

            if (Settings.Items.Any())
            {
                await SaveSettingsAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导入 Excel 失败: {FilePath}", dialog.FileName);
            _notificationService.ShowError($"导入 Excel 文件失败: {ex.Message}");
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

    private async Task SaveSettingsAsync()
    {
        try
        {
            await Task.Run(() => _settingsService.SaveSettings(Settings));
            
            Application.Current.Dispatcher.Invoke(() => 
            {
                _notificationService.ShowSuccessWithToken("翻板机配置已自动保存", "SettingWindowGrowl");
            });

            // 保存成功后，发布一次更新，确保服务获取到最新的配置
            _eventAggregator.GetEvent<PlateTurnoverSettingsUpdatedEvent>().Publish(Settings);
            Log.Information("翻板机配置已自动保存并发布更新。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "自动保存翻板机配置失败");
            Application.Current.Dispatcher.Invoke(() => 
            {
                _notificationService.ShowErrorWithToken("自动保存配置失败: " + ex.Message, "SettingWindowGrowl");
            });
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
        DebouncedAutoSave();
    }
    
    // --- 深度监听逻辑 ---

    private void SubscribeToItemChanges(ObservableCollection<PlateTurnoverItem> items)
    {
        if (items == null) return;
        items.CollectionChanged += OnItemsCollectionChanged;
        foreach (var item in items)
        {
            item.PropertyChanged += OnItemPropertyChanged;
        }
    }

    private void UnsubscribeFromItemChanges(ObservableCollection<PlateTurnoverItem> items)
    {
        if (items == null) return;
        items.CollectionChanged -= OnItemsCollectionChanged;
        foreach (var item in items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
    }
    
    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (INotifyPropertyChanged item in e.OldItems)
                item.PropertyChanged -= OnItemPropertyChanged;
        }
        if (e.NewItems != null)
        {
            foreach (INotifyPropertyChanged item in e.NewItems)
                item.PropertyChanged += OnItemPropertyChanged;
        }
        DebouncedAutoSave();
    }
    
    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        DebouncedAutoSave();
    }

    private async void DebouncedAutoSave()
    {
        try
        {
            _autoSaveCts?.Cancel(); // Cancel previous pending save
            _autoSaveCts?.Dispose();
            _autoSaveCts = new CancellationTokenSource();
            var token = _autoSaveCts.Token;

            await Task.Delay(1000, token); // Wait for 1 second

            // If not cancelled, proceed to save
            await SaveSettingsAsync();
        }
        catch (OperationCanceledException)
        {
            // This is expected if the user makes another change quickly.
            Log.Debug("自动保存操作被新的修改取消。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DebouncedAutoSave 发生意外错误。");
        }
    }

    // 释放资源，取消事件订阅
    public void Dispose()
    {
        if (_settings != null)
        {
            _settings.PropertyChanged -= Settings_PropertyChanged;
            UnsubscribeFromItemChanges(_settings.Items);
        }

        if (_autoSaveCts != null)
        {
            if (!_autoSaveCts.IsCancellationRequested)
            {
                _autoSaveCts.Cancel();
            }
            _autoSaveCts.Dispose();
            _autoSaveCts = null; // 确保被清理
        }
        
        GC.SuppressFinalize(this); // 通知GC不需要调用Finalize
        Log.Debug("PlateTurnoverSettingsViewModel disposed.");
    }

     // 处理 Settings 对象本身被替换的情况，重新订阅事件
     private void Settings_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
     {
         if (e.PropertyName == nameof(PlateTurnoverSettings.ChuteCount))
         {
             // 改为手动触发，但当其他直接属性（如ErrorChute）变更时，我们仍需发布更新
         }
         
         // 任何直接在Settings对象上的属性变更都应触发更新
         DebouncedAutoSave();
     }


    #endregion
}
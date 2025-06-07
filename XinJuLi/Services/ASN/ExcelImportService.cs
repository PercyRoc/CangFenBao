using System.IO;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using Serilog;
using XinJuLi.Models.ASN;

namespace XinJuLi.Services.ASN;

/// <summary>
/// Excel导入服务实现
/// 
/// 功能特性：
/// 1. 格口编号转换：配置中的格口编号映射为奇数格口（配置格口1→实际格口1，配置格口2→实际格口3，配置格口3→实际格口5）
/// 2. 智能数据识别：自动识别Excel表格中数据的起始行，支持有/无标题行的表格
/// 3. 多格式支持：支持.xls和.xlsx格式，支持数字和文本类型的格口编号和大区编码
/// </summary>
public class ExcelImportService : IExcelImportService
{
    /// <summary>
    /// 从Excel文件导入格口大区编码配置
    /// </summary>
    /// <param name="filePath">Excel文件路径</param>
    /// <returns>导入的配置，如果导入失败返回null</returns>
    public async Task<ChuteAreaConfig?> ImportChuteAreaConfigAsync(string filePath)
    {
        try
        {
            Log.Information("开始导入格口大区编码配置: {FilePath}", filePath);

            if (!File.Exists(filePath))
            {
                Log.Error("文件不存在: {FilePath}", filePath);
                return null;
            }

            var config = new ChuteAreaConfig();

            await Task.Run(() =>
            {
                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                
                // 根据文件扩展名选择不同的处理方式
                IWorkbook workbook;
                var extension = Path.GetExtension(filePath).ToLowerInvariant();
                
                if (extension == ".xls")
                {
                    workbook = new HSSFWorkbook(fileStream);
                }
                else if (extension == ".xlsx")
                {
                    workbook = new XSSFWorkbook(fileStream);
                }
                else
                {
                    throw new NotSupportedException($"不支持的文件格式: {extension}");
                }

                // 获取第一个工作表
                var sheet = workbook.GetSheetAt(0);
                if (sheet == null)
                {
                    throw new InvalidOperationException("Excel文件中没有找到工作表");
                }

                Log.Information("Excel工作表名称: {SheetName}, 行数: {RowCount}", 
                    sheet.SheetName, sheet.LastRowNum + 1);

                var importedCount = 0;
                var errorCount = 0;
                var dataStartRow = FindDataStartRow(sheet);

                Log.Information("检测到数据起始行: {StartRow}", dataStartRow + 1);

                // 从检测到的数据起始行开始读取
                for (var rowIndex = dataStartRow; rowIndex <= sheet.LastRowNum; rowIndex++)
                {
                    var row = sheet.GetRow(rowIndex);
                    if (row == null) continue;

                    try
                    {
                        // 读取格口编号（第一列）
                        var chuteCell = row.GetCell(0);
                        if (chuteCell == null) continue;

                        // 读取大区编码（第二列）
                        var areaCodeCell = row.GetCell(1);
                        if (areaCodeCell == null) continue;

                        // 解析格口编号
                        int configChuteNumber;
                        if (chuteCell.CellType == CellType.Numeric)
                        {
                            configChuteNumber = (int)chuteCell.NumericCellValue;
                        }
                        else if (chuteCell.CellType == CellType.String)
                        {
                            if (!int.TryParse(chuteCell.StringCellValue, out configChuteNumber))
                            {
                                Log.Warning("第{Row}行格口编号格式错误: {Value}", rowIndex + 1, chuteCell.StringCellValue);
                                errorCount++;
                                continue;
                            }
                        }
                        else
                        {
                            Log.Warning("第{Row}行格口编号类型不支持: {Type}", rowIndex + 1, chuteCell.CellType);
                            errorCount++;
                            continue;
                        }

                        // 转换格口编号：配置中的格口号映射为奇数格口
                        // 配置中的格口1 → 实际格口1，配置中的格口2 → 实际格口3，配置中的格口3 → 实际格口5
                        int chuteNumber = 2 * configChuteNumber - 1;

                        // 解析大区编码
                        string areaCode;
                        if (areaCodeCell.CellType == CellType.String)
                        {
                            areaCode = areaCodeCell.StringCellValue?.Trim() ?? string.Empty;
                        }
                        else if (areaCodeCell.CellType == CellType.Numeric)
                        {
                            areaCode = areaCodeCell.NumericCellValue.ToString();
                        }
                        else
                        {
                            Log.Warning("第{Row}行大区编码类型不支持: {Type}", rowIndex + 1, areaCodeCell.CellType);
                            errorCount++;
                            continue;
                        }

                        // 验证数据
                        if (configChuteNumber <= 0)
                        {
                            Log.Warning("第{Row}行配置格口编号无效: {Value}", rowIndex + 1, configChuteNumber);
                            errorCount++;
                            continue;
                        }

                        if (chuteNumber <= 0)
                        {
                            Log.Warning("第{Row}行转换后格口编号无效: 配置{ConfigChute} → 实际{ActualChute}", 
                                rowIndex + 1, configChuteNumber, chuteNumber);
                            errorCount++;
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(areaCode))
                        {
                            Log.Warning("第{Row}行大区编码为空", rowIndex + 1);
                            errorCount++;
                            continue;
                        }

                        // 添加到配置
                        config.AddOrUpdateItem(chuteNumber, areaCode);
                        importedCount++;

                        Log.Debug("导入配置项: 配置格口{ConfigChute} → 实际格口{ActualChute} -> 大区{AreaCode}", 
                            configChuteNumber, chuteNumber, areaCode);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "处理第{Row}行数据时发生错误", rowIndex + 1);
                        errorCount++;
                    }
                }

                Log.Information("格口大区编码配置导入完成: 成功{SuccessCount}项, 错误{ErrorCount}项", 
                    importedCount, errorCount);
            });

            if (config.Items.Count == 0)
            {
                Log.Warning("没有导入任何有效的配置项");
                return null;
            }

            Log.Information("成功导入{Count}个格口大区编码配置项", config.Items.Count);
            return config;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导入格口大区编码配置时发生错误: {FilePath}", filePath);
            return null;
        }
    }

    /// <summary>
    /// 智能识别Excel表格中数据的起始行
    /// </summary>
    /// <param name="sheet">工作表</param>
    /// <returns>数据起始行索引（0-based）</returns>
    private int FindDataStartRow(ISheet sheet)
    {
        try
        {
            // 扫描前10行，寻找第一个包含有效数据的行
            var maxScanRows = Math.Min(10, sheet.LastRowNum + 1);
            
            for (var rowIndex = 0; rowIndex < maxScanRows; rowIndex++)
            {
                var row = sheet.GetRow(rowIndex);
                if (row == null) continue;

                // 检查第一列和第二列是否包含有效数据
                var isValidDataRow = IsValidDataRow(row, rowIndex);
                if (isValidDataRow)
                {
                    Log.Debug("在第{Row}行找到有效数据", rowIndex + 1);
                    return rowIndex;
                }
            }

            // 如果没有找到有效数据行，默认从第1行开始（索引0）
            Log.Warning("未找到有效数据行，默认从第1行开始读取");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "识别数据起始行时发生错误，默认从第1行开始");
            return 0;
        }
    }

    /// <summary>
    /// 检查指定行是否为有效的数据行
    /// </summary>
    /// <param name="row">行对象</param>
    /// <param name="rowIndex">行索引</param>
    /// <returns>是否为有效数据行</returns>
    private bool IsValidDataRow(IRow row, int rowIndex)
    {
        try
        {
            // 获取第一列（格口编号）
            var chuteCell = row.GetCell(0);
            if (chuteCell == null) return false;

            // 获取第二列（大区编码）
            var areaCodeCell = row.GetCell(1);
            if (areaCodeCell == null) return false;

            // 检查第一列是否为有效的格口编号
            bool isValidChuteNumber = false;
            if (chuteCell.CellType == CellType.Numeric)
            {
                var numericValue = chuteCell.NumericCellValue;
                isValidChuteNumber = numericValue > 0 && numericValue <= 1000; // 合理的格口编号范围
            }
            else if (chuteCell.CellType == CellType.String)
            {
                var stringValue = chuteCell.StringCellValue?.Trim();
                if (int.TryParse(stringValue, out var parsedValue))
                {
                    isValidChuteNumber = parsedValue > 0 && parsedValue <= 1000;
                }
                else
                {
                    // 检查是否为常见的标题文字
                    var lowerValue = stringValue?.ToLower();
                    if (lowerValue == "格口" || lowerValue == "chute" || lowerValue == "格口编号" || 
                        lowerValue == "序号" || lowerValue == "编号" || lowerValue == "number")
                    {
                        Log.Debug("第{Row}行第1列包含标题文字: {Value}", rowIndex + 1, stringValue);
                        return false; // 这是标题行
                    }
                }
            }

            // 检查第二列是否为有效的大区编码
            bool isValidAreaCode = false;
            if (areaCodeCell.CellType == CellType.String)
            {
                var areaCodeValue = areaCodeCell.StringCellValue?.Trim();
                if (!string.IsNullOrWhiteSpace(areaCodeValue))
                {
                    // 检查是否为常见的标题文字
                    var lowerValue = areaCodeValue.ToLower();
                    if (lowerValue == "大区" || lowerValue == "大区编码" || lowerValue == "区域" || 
                        lowerValue == "area" || lowerValue == "code" || lowerValue == "区域编码")
                    {
                        Log.Debug("第{Row}行第2列包含标题文字: {Value}", rowIndex + 1, areaCodeValue);
                        return false; // 这是标题行
                    }
                    else
                    {
                        // 有效的大区编码通常是短字符串（1-10个字符）
                        isValidAreaCode = areaCodeValue.Length >= 1 && areaCodeValue.Length <= 10;
                    }
                }
            }
            else if (areaCodeCell.CellType == CellType.Numeric)
            {
                // 大区编码也可能是数字
                isValidAreaCode = true;
            }

            var isValid = isValidChuteNumber && isValidAreaCode;
            Log.Debug("第{Row}行数据检查: 格口编号有效={ChuteValid}, 大区编码有效={AreaValid}, 总体有效={Valid}", 
                rowIndex + 1, isValidChuteNumber, isValidAreaCode, isValid);

            return isValid;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "检查第{Row}行数据有效性时发生错误", rowIndex + 1);
            return false;
        }
    }
} 
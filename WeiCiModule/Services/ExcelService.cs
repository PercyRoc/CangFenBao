using System.IO;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using NPOI.HSSF.UserModel;
using WeiCiModule.Models;
using Serilog;
using System.Text;

namespace WeiCiModule.Services
{
    public class ExcelService
    {
        /// <summary>
        /// Export branch settings data to Excel or CSV file
        /// </summary>
        /// <param name="filePath">Export path</param>
        /// <param name="data">Data to export</param>
        /// <returns>Success status</returns>
        public static bool ExportToExcel(string filePath, List<ChuteSettingData?> data)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            
            return extension switch
            {
                ".csv" => ExportToCsv(filePath, data),
                ".xlsx" or ".xls" => ExportToExcelFile(filePath, data),
                _ => throw new NotSupportedException($"不支持的文件格式: {extension}")
            };
        }

        /// <summary>
        /// Export branch settings data to CSV file
        /// </summary>
        /// <param name="filePath">CSV file path</param>
        /// <param name="data">Data to export</param>
        /// <returns>Success status</returns>
        private static bool ExportToCsv(string filePath, List<ChuteSettingData?> data)
        {
            try
            {
                using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
                
                // 写入表头
                writer.WriteLine("Bin,BranchCode,Branch");
                
                // 写入数据
                foreach (var item in data)
                {
                    if (item == null) continue;
                    
                    // 处理可能包含逗号的字段
                    var branchCode = EscapeCsvField(item.BranchCode);
                    var branch = EscapeCsvField(item.Branch);
                    
                    writer.WriteLine($"{item.SN},{branchCode},{branch}");
                }
                
                Log.Information("成功导出格口设置到CSV文件: {FilePath}", filePath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "导出CSV文件时发生错误: {FilePath}", filePath);
                return false;
            }
        }

        /// <summary>
        /// Escape CSV field if it contains commas, quotes, or newlines
        /// </summary>
        /// <param name="field">Field to escape</param>
        /// <returns>Escaped field</returns>
        private static string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field))
                return string.Empty;
                
            if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            {
                // 转义引号并用引号包围
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }
            
            return field;
        }

        /// <summary>
        /// Export branch settings data to Excel file (original implementation)
        /// </summary>
        /// <param name="filePath">Excel file path</param>
        /// <param name="data">Data to export</param>
        /// <returns>Success status</returns>
        private static bool ExportToExcelFile(string filePath, List<ChuteSettingData?> data)
        {
            try
            {
                IWorkbook workbook;
                // Create different Workbook based on file extension
                if (Path.GetExtension(filePath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    workbook = new XSSFWorkbook();
                }
                else
                {
                    workbook = new HSSFWorkbook();
                }

                // Create worksheet
                ISheet sheet = workbook.CreateSheet("Branch Settings");

                // Create header row
                IRow headerRow = sheet.CreateRow(0);
                headerRow.CreateCell(0).SetCellValue("Bin");
                headerRow.CreateCell(1).SetCellValue("Branch Code");
                headerRow.CreateCell(2).SetCellValue("Branch");

                // Fill data
                int rowIndex = 1;
                foreach (var item in data)
                {
                    IRow dataRow = sheet.CreateRow(rowIndex++);
                    _ = dataRow.CreateCell(0).SetCellValue(item.SN);
                    dataRow.CreateCell(1).SetCellValue(item.BranchCode);
                    dataRow.CreateCell(2).SetCellValue(item.Branch);
                }

                // Auto-adjust column width
                for (int i = 0; i < 3; i++)
                {
                    sheet.AutoSizeColumn(i);
                }

                // Write to file
                using (FileStream fs = new(filePath, FileMode.Create, FileAccess.Write))
                {
                    workbook.Write(fs);
                }

                Log.Information("Successfully exported branch settings to Excel file: {FilePath}", filePath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error exporting Excel file: {FilePath}", filePath);
                return false;
            }
        }

        /// <summary>
        /// Import branch settings data from Excel or CSV file
        /// </summary>
        /// <param name="filePath">Excel or CSV file path</param>
        /// <returns>Imported data list</returns>
        public static List<ChuteSettingData> ImportFromExcel(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            
            return extension switch
            {
                ".csv" => ImportFromCsv(filePath),
                ".xlsx" or ".xls" => ImportFromExcelFile(filePath),
                _ => throw new NotSupportedException($"不支持的文件格式: {extension}")
            };
        }

        /// <summary>
        /// Import branch settings data from CSV file
        /// </summary>
        /// <param name="filePath">CSV file path</param>
        /// <returns>Imported data list</returns>
        private static List<ChuteSettingData> ImportFromCsv(string filePath)
        {
            var result = new List<ChuteSettingData>();
            try
            {
                var lines = File.ReadAllLines(filePath);
                if (lines.Length == 0)
                {
                    Log.Warning("CSV文件为空: {FilePath}", filePath);
                    return result;
                }

                // 解析表头
                var headers = ParseCsvLine(lines[0]);
                var headerMap = new Dictionary<string, int>();
                for (int i = 0; i < headers.Length; i++)
                {
                    var header = headers[i].Trim();
                    if (!string.IsNullOrEmpty(header))
                    {
                        headerMap[header] = i;
                    }
                }

                // 查找列索引
                if (!headerMap.TryGetValue("SN", out int snCol) && !headerMap.TryGetValue("Bin", out snCol))
                {
                    Log.Warning("未找到'SN'或'Bin'列，将使用第0列");
                    snCol = 0;
                }
                if (!headerMap.TryGetValue("BranchCode", out int codeCol))
                {
                    Log.Warning("未找到'BranchCode'列，将使用第1列");
                    codeCol = 1;
                }
                if (!headerMap.TryGetValue("Branch", out int nameCol) && !headerMap.TryGetValue("Name", out nameCol))
                {
                    Log.Warning("未找到'Branch'或'Name'列，将使用第2列");
                    nameCol = 2;
                }

                // 解析数据行
                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    var columns = ParseCsvLine(line);
                    if (columns.Length == 0) continue;

                    var data = new ChuteSettingData();

                    // 读取SN
                    if (snCol < columns.Length && int.TryParse(columns[snCol], out int sn))
                    {
                        data.SN = sn;
                    }
                    else
                    {
                        data.SN = i; // 使用行号作为后备
                    }

                    // 读取分支代码
                    data.BranchCode = codeCol < columns.Length ? columns[codeCol].Trim() : string.Empty;
                    
                    // 读取分支名称
                    data.Branch = nameCol < columns.Length ? columns[nameCol].Trim() : string.Empty;

                    if (!string.IsNullOrWhiteSpace(data.BranchCode) || !string.IsNullOrWhiteSpace(data.Branch))
                    {
                        result.Add(data);
                    }
                }

                Log.Information("成功从CSV文件导入{Count}条记录: {FilePath}", result.Count, filePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "导入CSV文件时发生错误: {FilePath}", filePath);
                throw;
            }

            return result;
        }

        /// <summary>
        /// Parse CSV line handling quoted fields and commas
        /// </summary>
        /// <param name="line">CSV line</param>
        /// <returns>Array of fields</returns>
        private static string[] ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var currentField = new StringBuilder();
            bool inQuotes = false;
            
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        // 转义的引号
                        currentField.Append('"');
                        i++; // 跳过下一个引号
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    fields.Add(currentField.ToString());
                    currentField.Clear();
                }
                else
                {
                    currentField.Append(c);
                }
            }
            
            fields.Add(currentField.ToString());
            return [.. fields];
        }

        /// <summary>
        /// Import branch settings data from Excel file (original implementation)
        /// </summary>
        /// <param name="filePath">Excel file path</param>
        /// <returns>Imported data list</returns>
        private static List<ChuteSettingData> ImportFromExcelFile(string filePath)
        {
            var result = new List<ChuteSettingData>();
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                IWorkbook workbook = Path.GetExtension(filePath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                    ? new XSSFWorkbook(fs)
                    : new HSSFWorkbook(fs);

                ISheet sheet = workbook.GetSheetAt(0);
                if (sheet == null)
                {
                    Log.Warning("No worksheet found in Excel file: {FilePath}", filePath);
                    return result;
                }

                IRow headerRow = sheet.GetRow(0);
                if (headerRow == null)
                {
                    Log.Warning("No header row found in Excel file: {FilePath}", filePath);
                    return result;
                }

                // Create a map from header name to column index
                var headerMap = new Dictionary<string, int>();
                for (int i = 0; i < headerRow.LastCellNum; i++)
                {
                    string? header = headerRow.GetCell(i)?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(header))
                    {
                        headerMap[header] = i;
                    }
                }

                // Try to find columns for our required fields using flexible names
                if (!headerMap.TryGetValue("SN", out int snCol) && !headerMap.TryGetValue("Bin", out snCol))
                {
                    Log.Warning("Header 'SN' or 'Bin' not found. Will try to read from column 0.");
                    snCol = 0;
                }
                if (!headerMap.TryGetValue("BranchCode", out int codeCol))
                {
                    Log.Warning("Header 'BranchCode' not found. Will try to read from column 1.");
                    codeCol = 1;
                }
                if (!headerMap.TryGetValue("Branch", out int nameCol) && !headerMap.TryGetValue("Name", out nameCol))
                {
                    Log.Warning("Header 'Branch' or 'Name' not found. Will try to read from column 2.");
                    nameCol = 2;
                }
                
                // Iterate through data rows (starting from second row, skipping header)
                for (int i = 1; i <= sheet.LastRowNum; i++)
                {
                    IRow row = sheet.GetRow(i);
                    if (row == null) continue;

                    var data = new ChuteSettingData();

                    // Read SN from the determined column
                    var snCell = row.GetCell(snCol);
                    if (snCell != null)
                    {
                        if (snCell.CellType == CellType.Numeric)
                        {
                            data.SN = (int)snCell.NumericCellValue;
                        }
                        else if (int.TryParse(snCell.ToString(), out int sn))
                        {
                            data.SN = sn;
                        }
                        else
                        {
                            data.SN = i; // Use row index as fallback
                        }
                    }
                    else
                    {
                        data.SN = i; // Fallback
                    }

                    // Read Branch Code from the determined column
                    data.BranchCode = row.GetCell(codeCol)?.ToString() ?? string.Empty;
                    
                    // Read Branch from the determined column
                    data.Branch = row.GetCell(nameCol)?.ToString() ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(data.BranchCode) || !string.IsNullOrWhiteSpace(data.Branch))
                    {
                        result.Add(data);
                    }
                }

                Log.Information("Successfully imported {Count} records from Excel file: {FilePath}", result.Count, filePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error importing Excel file: {FilePath}", filePath);
                throw; // Rethrow to be caught by the ViewModel
            }

            return result;
        }
    }
} 
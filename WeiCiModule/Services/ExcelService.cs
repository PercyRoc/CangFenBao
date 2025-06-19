using System;
using System.Collections.Generic;
using System.IO;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using NPOI.HSSF.UserModel;
using WeiCiModule.Models;
using Serilog;

namespace WeiCiModule.Services
{
    public class ExcelService
    {
        /// <summary>
        /// Export branch settings data to Excel file
        /// </summary>
        /// <param name="filePath">Export path</param>
        /// <param name="data">Data to export</param>
        /// <returns>Success status</returns>
        public bool ExportToExcel(string filePath, List<ChuteSettingData?> data)
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
                    dataRow.CreateCell(0).SetCellValue(item.SN);
                    dataRow.CreateCell(1).SetCellValue(item.BranchCode);
                    dataRow.CreateCell(2).SetCellValue(item.Branch);
                }

                // Auto-adjust column width
                for (int i = 0; i < 3; i++)
                {
                    sheet.AutoSizeColumn(i);
                }

                // Write to file
                using (FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
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
        /// Import branch settings data from Excel file
        /// </summary>
        /// <param name="filePath">Excel file path</param>
        /// <returns>Imported data list</returns>
        public List<ChuteSettingData> ImportFromExcel(string filePath)
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
                    string header = headerRow.GetCell(i)?.ToString()?.Trim();
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
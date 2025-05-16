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
                headerRow.CreateCell(0).SetCellValue("S/N");
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
                using FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                IWorkbook workbook;

                // Determine Excel version based on file extension
                if (Path.GetExtension(filePath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    workbook = new XSSFWorkbook(fs);
                }
                else
                {
                    workbook = new HSSFWorkbook(fs);
                }

                // Get first worksheet
                ISheet sheet = workbook.GetSheetAt(0);
                if (sheet == null)
                {
                    Log.Warning("No worksheet found in Excel file: {FilePath}", filePath);
                    return result;
                }

                // Check if header row exists
                if (sheet.LastRowNum < 1)
                {
                    Log.Warning("No data rows found in Excel file: {FilePath}", filePath);
                    return result;
                }

                // Iterate through data rows (starting from second row, skipping header)
                for (int i = 1; i <= sheet.LastRowNum; i++)
                {
                    IRow row = sheet.GetRow(i);
                    if (row == null) continue;

                    var data = new ChuteSettingData();
                    
                    // Try to read S/N
                    var snCell = row.GetCell(0);
                    if (snCell != null)
                    {
                        if (snCell.CellType == CellType.Numeric)
                        {
                            data.SN = (int)snCell.NumericCellValue;
                        }
                        else
                        {
                            // Try to convert text to number
                            if (int.TryParse(snCell.ToString(), out int sn))
                            {
                                data.SN = sn;
                            }
                            else
                            {
                                data.SN = i; // Use row number as fallback
                            }
                        }
                    }
                    else
                    {
                        data.SN = i; // Use row number as fallback
                    }

                    // Read Branch Code
                    var codeCell = row.GetCell(1);
                    if (codeCell != null)
                    {
                        data.BranchCode = codeCell.ToString();
                    }

                    // Read Branch
                    var nameCell = row.GetCell(2);
                    if (nameCell != null)
                    {
                        data.Branch = nameCell.ToString();
                    }

                    result.Add(data);
                }

                Log.Information("Successfully imported {Count} records from Excel file: {FilePath}", result.Count, filePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error importing Excel file: {FilePath}", filePath);
            }

            return result;
        }
    }
} 
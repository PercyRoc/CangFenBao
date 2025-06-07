using XinJuLi.Models.ASN;

namespace XinJuLi.Services.ASN;

/// <summary>
/// Excel导入服务接口
/// </summary>
public interface IExcelImportService
{
    /// <summary>
    /// 从Excel文件导入格口大区编码配置
    /// 
    /// 特性：
    /// - 自动识别数据起始行（支持第1行、第2行、第3行等不同格式的Excel）
    /// - 自动转换格口编号（配置编号×2为实际系统格口编号）
    /// - 支持.xls和.xlsx格式文件
    /// </summary>
    /// <param name="filePath">Excel文件路径</param>
    /// <returns>导入的配置，如果导入失败返回null</returns>
    Task<ChuteAreaConfig?> ImportChuteAreaConfigAsync(string filePath);
} 
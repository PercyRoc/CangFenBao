using Common.Services.Settings;

namespace XinJuLi.Models.ASN;

/// <summary>
/// 格口大区编码配置项
/// </summary>
public class ChuteAreaConfigItem
{
    /// <summary>
    /// 格口编号（实际系统中的格口编号，已从配置中的编号转换：配置编号×2）
    /// </summary>
    public int ChuteNumber { get; set; }

    /// <summary>
    /// 大区编码
    /// </summary>
    public string AreaCode { get; set; } = string.Empty;
}

/// <summary>
/// 格口大区编码配置
/// </summary>
[Configuration("ChuteAreaConfig")]
public class ChuteAreaConfig
{
    /// <summary>
    /// 配置项列表
    /// </summary>
    public List<ChuteAreaConfigItem> Items { get; set; } = [];

    /// <summary>
    /// 配置导入时间
    /// </summary>
    public DateTime ImportTime { get; set; } = DateTime.Now;

    /// <summary>
    /// 配置版本
    /// </summary>
    public string Version { get; set; } = "1.0";

    /// <summary>
    /// 根据大区编码查找对应的格口编号
    /// </summary>
    /// <param name="areaCode">大区编码</param>
    /// <returns>格口编号，如果没找到返回-1</returns>
    public int FindChuteByAreaCode(string areaCode)
    {
        var item = Items.FirstOrDefault(x => x.AreaCode.Equals(areaCode, StringComparison.OrdinalIgnoreCase));
        return item?.ChuteNumber ?? -1;
    }

    /// <summary>
    /// 添加或更新配置项
    /// </summary>
    /// <param name="chuteNumber">格口编号</param>
    /// <param name="areaCode">大区编码</param>
    public void AddOrUpdateItem(int chuteNumber, string areaCode)
    {
        var existingItem = Items.FirstOrDefault(x => x.ChuteNumber == chuteNumber);
        if (existingItem != null)
        {
            existingItem.AreaCode = areaCode;
        }
        else
        {
            Items.Add(new ChuteAreaConfigItem
            {
                ChuteNumber = chuteNumber,
                AreaCode = areaCode
            });
        }
    }

    /// <summary>
    /// 清空所有配置项
    /// </summary>
    public void Clear()
    {
        Items.Clear();
        ImportTime = DateTime.Now;
    }
} 
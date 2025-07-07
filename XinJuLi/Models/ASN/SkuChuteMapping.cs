using Common.Services.Settings;

namespace XinJuLi.Models.ASN;

/// <summary>
///     SKU格口映射配置
///     用于持久化保存SKU到格口的映射关系，防止断电等异常情况导致数据丢失
/// </summary>
[Configuration("SkuChuteMapping.json")]
public class SkuChuteMapping
{
    /// <summary>
    ///     映射项列表
    /// </summary>
    public List<SkuChuteMappingItem> Items { get; set; } = [];

    /// <summary>
    ///     关联的ASN单号
    /// </summary>
    public string AsnOrderCode { get; set; } = string.Empty;

    /// <summary>
    ///     关联的车号
    /// </summary>
    public string CarCode { get; set; } = string.Empty;

    /// <summary>
    ///     保存时间
    /// </summary>
    public DateTime SaveTime { get; set; } = DateTime.Now;

    /// <summary>
    ///     添加或更新映射项
    /// </summary>
    /// <param name="sku">SKU</param>
    /// <param name="chuteNumber">格口号</param>
    public void AddOrUpdateItem(string sku, int chuteNumber)
    {
        var existingItem = Items.FirstOrDefault(x => x.Sku == sku);
        if (existingItem != null)
        {
            existingItem.ChuteNumber = chuteNumber;
        }
        else
        {
            Items.Add(new SkuChuteMappingItem
            {
                Sku = sku,
                ChuteNumber = chuteNumber
            });
        }
    }

    /// <summary>
    ///     根据SKU查找格口号
    /// </summary>
    /// <param name="sku">SKU</param>
    /// <returns>格口号，如果未找到返回-1</returns>
    public int FindChuteNumber(string sku)
    {
        var item = Items.FirstOrDefault(x => x.Sku == sku);
        return item?.ChuteNumber ?? -1;
    }

    /// <summary>
    ///     清空所有映射项
    /// </summary>
    public void Clear()
    {
        Items.Clear();
        AsnOrderCode = string.Empty;
        CarCode = string.Empty;
        SaveTime = DateTime.Now;
    }
}

/// <summary>
///     SKU格口映射项
/// </summary>
public class SkuChuteMappingItem
{
    /// <summary>
    ///     SKU
    /// </summary>
    public string Sku { get; set; } = string.Empty;

    /// <summary>
    ///     格口号
    /// </summary>
    public int ChuteNumber { get; set; }
}
namespace History.Data;

/// <summary>
///     包裹历史数据服务接口
/// </summary>
public interface IPackageHistoryDataService
{
    /// <summary>
    ///     初始化历史数据库服务，确保数据库和当月/下月表已创建。
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    ///     添加历史包裹记录
    /// </summary>
    /// <param name="package">要添加的包裹历史记录</param>
    Task AddPackageAsync(PackageHistoryRecord package);

    /// <summary>
    ///     根据条码和精确创建时间查询单个历史包裹记录。
    /// </summary>
    /// <param name="barcode">要查询的条码</param>
    /// <param name="createTime">包裹的精确创建时间</param>
    /// <returns>找到的包裹历史记录，如果不存在则为 null。</returns>
    Task<PackageHistoryRecord?> GetPackageByBarcodeAndTimeAsync(string barcode, DateTime createTime);

    /// <summary>
    ///     根据指定条件异步查询历史包裹记录，并支持分页。
    /// </summary>
    /// <param name="startDate">查询的开始日期（可选）</param>
    /// <param name="endDate">查询的结束日期（可选）</param>
    /// <param name="barcodeFilter">条码过滤器（可选，部分匹配）</param>
    /// <param name="pageNumber">页码（从1开始）</param>
    /// <param name="pageSize">每页记录数</param>
    /// <returns>包含当页记录和总记录数的元组。</returns>
    Task<(IEnumerable<PackageHistoryRecord> Records, int TotalCount)> GetPackagesAsync(DateTime? startDate, DateTime? endDate, string? barcodeFilter, int pageNumber, int pageSize);
    
    /// <summary>
    ///     更新历史包裹记录。
    /// </summary>
    /// <param name="package">包含更新信息的包裹历史记录对象。必须提供有效的 Id 和 CreateTime 以定位记录。</param>
    Task UpdatePackageAsync(PackageHistoryRecord package);

    /// <summary>
    ///     根据ID和创建时间删除历史包裹记录。
    /// </summary>
    /// <param name="id">要删除的包裹记录的ID。</param>
    /// <param name="createTime">包裹记录的创建时间，用于定位正确的月份表。</param>
    Task DeletePackageAsync(long id, DateTime createTime);
    
    /// <summary>
    ///     检查并修复历史数据库的表结构。
    /// </summary>
    Task RepairDatabaseAsync();

    /// <summary>
    ///     如果存在旧格式的表，则将其数据迁移到新格式的表。
    /// </summary>
    Task MigrateLegacyDataIfNeededAsync();

    /// <summary>
    ///     为指定的月份创建或确保对应的历史表存在。
    /// </summary>
    /// <param name="date">要为其创建表的月份中的任意一天。</param>
    Task CreateTableForMonthAsync(DateTime date);

    /// <summary>
    ///    清理指定保留月份之外的旧历史数据表。
    /// </summary>
    /// <param name="monthsToKeep">要保留的最近月份数。如果小于等于0，则不进行清理。</param>
    Task CleanupOldTablesAsync(int monthsToKeep);
} 
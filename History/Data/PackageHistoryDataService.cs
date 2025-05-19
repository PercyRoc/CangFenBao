using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Microsoft.Data.Sqlite;

namespace History.Data;

/// <summary>
///     包裹历史数据服务实现
/// </summary>
internal class PackageHistoryDataService : IPackageHistoryDataService
{
    private readonly string _dbPath;
    private readonly DbContextOptions<PackageHistoryDbContext> _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, bool> _tableExistsCache = new();
    private const string TablePrefix = "Package_"; // 表名前缀
    private const string LegacyTablePrefix = "Packages_"; // 旧表名前缀
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    ///     构造函数
    /// </summary>
    public PackageHistoryDataService(DbContextOptions<PackageHistoryDbContext> options, ILogger logger)
    {
        _logger = logger.ForContext<PackageHistoryDataService>();
        // 数据库文件将存储在应用程序根目录下的 Data/History 子目录中
        // 例如：C:\YourApp\Data\History\PackageHistory.db
        var dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        if (!Directory.Exists(dataDirectory))
        {
            try { Directory.CreateDirectory(dataDirectory); _logger.Information("创建历史数据目录: {Directory}", dataDirectory); }
            catch (Exception ex) { _logger.Error(ex, "创建历史数据目录 {Directory} 失败。", dataDirectory); }
        }
        _dbPath = Path.Combine(dataDirectory, "Package.db");
        _options = options;
        _logger.Information("PackageHistoryDataService 已实例化。数据库路径: {DbPath}", _dbPath);
    }

    private static string GetTableName(DateTime date)
    {
        return $"{TablePrefix}{date:yyyyMM}";
    }

    private PackageHistoryDbContext CreateDbContext(DateTime? forDate = null)
    {
        return new PackageHistoryDbContext(_options, forDate, _logger);
    }

    public async Task InitializeAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            _logger.Information("正在初始化历史数据库服务 (InitializeAsync)...数据库路径: {DbPath}", _dbPath);
            await using var context = CreateDbContext();
            await context.Database.EnsureCreatedAsync();
            _logger.Information("历史数据库文件 EnsureCreatedAsync 完成。");
            var today = DateTime.Today;
            await EnsureMonthlyTableExistsInternal(today, context);
            await EnsureMonthlyTableExistsInternal(today.AddMonths(1), context);
             // 在初始化末尾执行数据迁移检查
            await MigrateLegacyDataIfNeededAsync();
            _logger.Information("历史数据库服务初始化完成 (InitializeAsync)。");
        }
        catch (Exception ex) { _logger.Error(ex, "初始化历史数据库服务 (InitializeAsync) 失败。"); throw; }
        finally { _semaphore.Release(); }
    }

    public async Task AddPackageAsync(PackageHistoryRecord record)
    {
        try
        {
            if (record.CreateTime == DateTime.MinValue)
            { _logger.Warning("历史服务：包裹 {Barcode} CreateTime 为 MinValue，使用当前时间。", record.Barcode); record.CreateTime = DateTime.Now; }
            else if (record.CreateTime > DateTime.Now.AddHours(1) || record.CreateTime < DateTime.Now.AddYears(-10))
            { _logger.Warning("历史服务：包裹 {Barcode} CreateTime ({OriginalTime}) 异常，使用当前时间。", record.Barcode, record.CreateTime.ToString("o")); record.CreateTime = DateTime.Now; }

            var tableName = GetTableName(record.CreateTime);
            await EnsureMonthlyTableExistsInternal(record.CreateTime);
            await using var dbContext = CreateDbContext(record.CreateTime);
            var existingRecord = await dbContext.Set<PackageHistoryRecord>().AsNoTracking().FirstOrDefaultAsync(r => r.Barcode == record.Barcode && r.CreateTime == record.CreateTime);
            if (existingRecord != null)
            { _logger.Information("历史服务：表 {TableName} 已存在条码 {Barcode} @ {CreateTime} (ID: {ExistingId})，跳过添加新记录 (ID: {NewId})。", tableName, record.Barcode, record.CreateTime.ToString("o"), existingRecord.Id, record.Id); return; }
            dbContext.Add(record);
            await dbContext.SaveChangesAsync();
            _logger.Debug("历史服务：添加包裹记录成功：{Barcode} @ {CreateTime} (ID: {Id})，存入表 {TableName}", record.Barcode, record.CreateTime.ToString("o"), record.Id, tableName);
        }
        catch (Exception ex) { _logger.Error(ex, "历史服务：添加包裹记录失败。Barcode: {Barcode}, CreateTime: {CreateTime}, ID: {Id}", record.Barcode, record.CreateTime.ToString("o"), record.Id); }
    }

    public async Task<PackageHistoryRecord?> GetPackageByBarcodeAndTimeAsync(string barcode, DateTime createTime)
    {
        if (string.IsNullOrEmpty(barcode)) return null;
        var tableName = GetTableName(createTime);
        try
        {
            await using var context = CreateDbContext(createTime);
            if (await TableExistsAsync(context, tableName))
                return await context.Set<PackageHistoryRecord>().AsNoTracking()
                    .FirstOrDefaultAsync(r => r.Barcode == barcode && r.CreateTime == createTime);
            _logger.Debug("GetPackageByBarcodeAndTimeAsync: 表 {TableName} 不存在，无法获取包裹 {Barcode}。", tableName, barcode); 
            return null;
        }
        catch (Exception ex) { _logger.Error(ex, "GetPackageByBarcodeAndTimeAsync: 获取包裹 {Barcode} @ {CreateTime} (表: {TableName}) 失败。", barcode, createTime.ToString("o"), tableName); return null; }
    }

    public async Task<(IEnumerable<PackageHistoryRecord> Records, int TotalCount)> GetPackagesAsync(DateTime? startDate, DateTime? endDate, string? barcodeFilter, int pageNumber, int pageSize)
    {
        if (pageNumber <= 0) pageNumber = 1;
        if (pageSize <= 0) pageSize = 1000;
        
        var allMatchingRecords = new List<PackageHistoryRecord>();
        var relevantTableNames = GetTableNamesForDateRange(startDate, endDate);

        if (relevantTableNames.Count == 0)
        { 
            _logger.Debug("GetPackagesAsync: 日期范围未找到相关历史表。S: {SDate}, E: {EDate}", startDate?.ToString("o"), endDate?.ToString("o")); 
            return ([], 0); 
        }

        try
        {
            foreach (var tableName in relevantTableNames)
            {
                if (!TryParseDateFromTableName(tableName, out var tableDate)) 
                { 
                    _logger.Warning("GetPackagesAsync: 无法从表名 {TableName} 解析日期，跳过。", tableName); 
                    continue; 
                }

                await using var context = CreateDbContext(tableDate);
                if (!await TableExistsAsync(context, tableName)) 
                { 
                    _logger.Debug("GetPackagesAsync: 表 {TableName} 不存在，跳过。", tableName); 
                    continue; 
                }
                
                var query = context.Set<PackageHistoryRecord>().AsNoTracking();

                if (startDate.HasValue) query = query.Where(p => p.CreateTime >= startDate.Value);
                if (endDate.HasValue) 
                { 
                    var endOfDay = endDate.Value.Date.AddDays(1).AddTicks(-1);
                    query = query.Where(p => p.CreateTime <= endOfDay); 
                }
                if (!string.IsNullOrWhiteSpace(barcodeFilter)) 
                { 
                    string upperBarcodeFilter = barcodeFilter.ToUpperInvariant(); 
                    query = query.Where(p => p.Barcode.ToUpper().Contains(upperBarcodeFilter)); 
                }
                
                try 
                { 
                    allMatchingRecords.AddRange(await query.ToListAsync()); 
                }
                catch (Exception ex) 
                { 
                    _logger.Error(ex, "GetPackagesAsync: 查询表 {TableName} 出错。S:{SDate}, E:{EDate}, BC:{BC}", tableName, startDate?.ToString("o"), endDate?.ToString("o"), barcodeFilter); 
                }
            }
        }
        catch (Exception ex) 
        { 
            _logger.Error(ex, "GetPackagesAsync: 检索历史记录顶层错误。S:{SDate}, E:{EDate}, BC:{BC}", startDate?.ToString("o"), endDate?.ToString("o"), barcodeFilter); 
            return ([], 0); 
        }
        
        var sortedRecordsList = allMatchingRecords.OrderByDescending(p => p.CreateTime).ToList();

        var totalCount = sortedRecordsList.Count;
        var pagedRecords = sortedRecordsList.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();
        
        _logger.Debug("GetPackagesAsync: Query OK. Total: {Total}. Page: {PageNum}/{PageSize}. Actual: {Actual}. S:{SDate}, E:{EDate}, BC:{BC}", totalCount, pageNumber, pageSize, pagedRecords.Count, startDate?.ToString("o"), endDate?.ToString("o"), barcodeFilter);
        return (pagedRecords, totalCount);
    }

    public async Task UpdatePackageAsync(PackageHistoryRecord package)
    {
        if (package.Id == 0) { _logger.Error("UpdatePackageAsync: 包裹ID为0，无法更新。Barcode: {Barcode}", package.Barcode); return; }
        
        var tableName = GetTableName(package.CreateTime);
        await _semaphore.WaitAsync();
        try
        {
            await EnsureMonthlyTableExistsInternal(package.CreateTime);
            await using var context = CreateDbContext(package.CreateTime);

            if (!await TableExistsAsync(context, tableName)) 
            { 
                _logger.Warning("UpdatePackageAsync: 表 {TableName} 不存在，无法更新包裹 ID: {Id}", tableName, package.Id); 
                return; 
            }
            
            var existingRecord = await context.Set<PackageHistoryRecord>().FirstOrDefaultAsync(r => r.Id == package.Id);
            if (existingRecord != null)
            { 
                context.Entry(existingRecord).CurrentValues.SetValues(package); 
                await context.SaveChangesAsync(); 
                _logger.Information("UpdatePackageAsync: 包裹 {Barcode} (ID: {Id}) 在表 {TableName} 更新成功。", package.Barcode, package.Id, tableName); 
            }
            else 
            { 
                _logger.Warning("UpdatePackageAsync: 更新包裹 {Barcode} (ID: {Id}) 在表 {TableName} 未找到。", package.Barcode, package.Id, tableName); 
            }
        }
        catch (Exception ex) { _logger.Error(ex, "UpdatePackageAsync: 更新包裹 {Barcode} (ID: {Id}) 失败。表: {TableName}", package.Barcode, package.Id, tableName); }
        finally { _semaphore.Release(); }
    }

    public async Task DeletePackageAsync(long id, DateTime createTime)
    {
        if (id == 0) { _logger.Error("DeletePackageAsync: 包裹ID为0，无法删除。CreateTime: {Time}", createTime.ToString("o")); return; }
        
        var tableName = GetTableName(createTime);
        await _semaphore.WaitAsync();
        try
        {
            await EnsureMonthlyTableExistsInternal(createTime);
            await using var context = CreateDbContext(createTime);

            if (!await TableExistsAsync(context, tableName)) 
            { 
                _logger.Warning("DeletePackageAsync: 表 {TableName} 不存在，无法删除包裹 ID: {Id}。", tableName, id); 
                return; 
            }
            
            var recordToDelete = await context.Set<PackageHistoryRecord>().FirstOrDefaultAsync(r => r.Id == id); 
            if (recordToDelete != null)
            { 
                context.Remove(recordToDelete); 
                await context.SaveChangesAsync(); 
                _logger.Information("DeletePackageAsync: 包裹 ID {Id} 已从表 {TableName} 删除。", id, tableName); 
            }
            else 
            { 
                _logger.Warning("DeletePackageAsync: 删除包裹 ID {Id} 在表 {TableName} 未找到。", id, tableName); 
            }
        }
        catch (Exception ex) { _logger.Error(ex, "DeletePackageAsync: 删除包裹 ID {Id} (表: {TableName}, Time: {Time}) 失败。", id, tableName, createTime.ToString("o")); }
        finally { _semaphore.Release(); }
    }

    public async Task RepairDatabaseAsync()
    {
        _logger.Information("RepairDatabaseAsync: 开始修复历史数据库结构...");
        await _semaphore.WaitAsync();
        try
        {
            var allKnownTables = await GetAllHistoricalTableNamesAsync(useSemaphore: false);
            if (allKnownTables.Count == 0) 
            { 
                _logger.Information("RepairDatabaseAsync: 未找到历史表。确保当月表存在..."); 
                await EnsureMonthlyTableExistsInternal(DateTime.Today);
                return; 
            }

            foreach (var tableName in allKnownTables)
            { 
                if (TryParseDateFromTableName(tableName, out var tableDate)) 
                { 
                    _logger.Information("RepairDatabaseAsync: 检查/修复表 {TableName}结构...", tableName); 
                    await EnsureMonthlyTableExistsInternal(tableDate);
                }
                else 
                { 
                    _logger.Warning("RepairDatabaseAsync: 表名 {TableName} 格式不正确，跳过修复。", tableName); 
                }
            }
            _logger.Information("RepairDatabaseAsync: 数据库修复检查完成。检查 {Count} 个表。", allKnownTables.Count);
        }
        catch (Exception ex) { _logger.Error(ex, "RepairDatabaseAsync: 修复数据库时出错。"); }
        finally { _semaphore.Release(); }
    }

    public async Task CreateTableForMonthAsync(DateTime date)
    {
        _logger.Information("CreateTableForMonthAsync: 请求为月份 {Month} 创建表。", date.ToString("yyyy-MM"));
        await EnsureMonthlyTableExistsInternal(date);
        _logger.Information("CreateTableForMonthAsync: 已确保月份 {Month} 的表存在/已创建。", date.ToString("yyyy-MM"));
    }

    public async Task CleanupOldTablesAsync(int monthsToKeep)
    {
        if (monthsToKeep <= 0) { _logger.Warning("CleanupOldTablesAsync: 保留月份数 ({Months}) 无效，不清理。", monthsToKeep); return; }
        
        await _semaphore.WaitAsync();
        try
        {
            _logger.Information("CleanupOldTablesAsync: 开始清理旧历史表，保留最近 {Months} 个月。", monthsToKeep);
            var cutoffDate = DateTime.Today.AddMonths(-monthsToKeep);
            var allTables = await GetAllHistoricalTableNamesAsync(useSemaphore: false);

            if (allTables.Count == 0) { _logger.Information("CleanupOldTablesAsync: 未找到历史表进行清理。"); return; }
            
            int deletedCount = 0;
            foreach (var tableName in allTables)
            {
                if (!TryParseDateFromTableName(tableName, out var tableDate)) continue;
                if (tableDate >= new DateTime(cutoffDate.Year, cutoffDate.Month, 1)) continue;
                _logger.Information("CleanupOldTablesAsync: 准备删除旧表: {Table} ({TDate})，早于截止 ({CODate})", tableName, tableDate.ToString("yyyy-MM"), new DateTime(cutoffDate.Year, cutoffDate.Month, 1).ToString("yyyy-MM")); 
                try 
                { 
                    await using var context = CreateDbContext(); 
                    var esc = tableName.Replace("`", "``"); 
                    await context.Database.ExecuteSqlAsync($"DROP TABLE IF EXISTS `{esc}`"); 
                    _tableExistsCache.TryRemove(tableName, out _); 
                    _logger.Information("CleanupOldTablesAsync: 旧表 {Table} 已删除。", tableName); 
                    deletedCount++; 
                }
                catch (Exception ex) { _logger.Error(ex, "CleanupOldTablesAsync: 删除旧表 {Table} 出错。", tableName); }
            }
            _logger.Information("CleanupOldTablesAsync: 旧历史表清理完成。删除 {Count} 个表。", deletedCount);
        }
        catch (Exception ex) { _logger.Error(ex, "CleanupOldTablesAsync: 清理旧历史表时发生顶层错误。"); }
        finally { _semaphore.Release(); }
    }

    // ---- 以下为私有辅助方法 ----

    private List<string> GetTableNamesForDateRange(DateTime? startDate, DateTime? endDate)
    {
        var tableNames = new HashSet<string>();
        var today = DateTime.Today;
        var effectiveStartDate = startDate ?? new DateTime(today.Year, today.Month, 1);
        var effectiveEndDate = endDate ?? new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        if (effectiveEndDate < effectiveStartDate) { _logger.Warning("GetTableNamesForDateRange: ED {ED:o} < SD {SD:o}. Using SD for ED.", effectiveEndDate, effectiveStartDate); effectiveEndDate = effectiveStartDate; }
        var currentMonthIterator = new DateTime(effectiveStartDate.Year, effectiveStartDate.Month, 1);
        while (currentMonthIterator <= effectiveEndDate)
        { tableNames.Add(GetTableName(currentMonthIterator)); var nextMonth = currentMonthIterator.AddMonths(1); if (nextMonth <= currentMonthIterator) { _logger.Error("GetTableNamesForDateRange: Month iteration error. C:{C:o}, N:{N:o}", currentMonthIterator, nextMonth); break; } currentMonthIterator = nextMonth; }
        return tableNames.ToList();
    }

    private async Task EnsureMonthlyTableExistsInternal(DateTime date, PackageHistoryDbContext? existingContext = null)
    {
        var tableName = GetTableName(date); 
        _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] Starting for date {Date}", tableName, date);
        if (_tableExistsCache.TryGetValue(tableName, out var exists) && exists)
        {
            _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] Found in cache and exists.", tableName);
            return;
        }
        _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] Not in cache or does not exist in cache.", tableName);

        try
        {
            if (_tableExistsCache.TryGetValue(tableName, out exists) && exists) // Double check after potential wait (if semaphore was here)
            {
                 _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] Found in cache after re-check.", tableName);
                 return;
            }

            PackageHistoryDbContext? contextToUse = null;
            bool disposeContext = false;

            if (existingContext != null && existingContext.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
            {
                contextToUse = existingContext;
                _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] Using passed DbContext.", tableName);
            }
            else
            {
                _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] Creating new DbContext.", tableName);
                contextToUse = CreateDbContext(date); 
                disposeContext = true;
                _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] New DbContext created.", tableName);
            }

            try
            {
                _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] Before contextToUse.Database.EnsureCreatedAsync().", tableName);
                await contextToUse.Database.EnsureCreatedAsync(); 
                _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] After contextToUse.Database.EnsureCreatedAsync().", tableName);

                var connection = contextToUse.Database.GetDbConnection();
                _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] Got DbConnection. Current state: {State}", tableName, connection.State);
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] Connection not open. Before connection.OpenAsync().", tableName);
                    await connection.OpenAsync();
                    _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] After connection.OpenAsync(). State: {State}", tableName, connection.State);
                }
                
                var escapedTableName = tableName.Replace("'", "''"); 
                string checkTableSql = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{escapedTableName}';";
                _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] Check table SQL: {Sql}", tableName, checkTableSql);
                
                await using (var command = connection.CreateCommand()) 
                { 
                    command.CommandText = checkTableSql;
                    _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] Before command.ExecuteScalarAsync().", tableName);
                    var result = await command.ExecuteScalarAsync(); 
                    _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] After command.ExecuteScalarAsync(). Result: {Result}", tableName, result);
                    
                    if (result == null || result == DBNull.Value) 
                    { 
                        _logger.Information("EnsureMonthlyTableExistsInternal: [{Table}] Table not found in sqlite_master, creating...", tableName); 
                        await CreateMonthlyTableAsync(contextToUse, tableName); 
                        _logger.Information("EnsureMonthlyTableExistsInternal: [{Table}] Finished calling CreateMonthlyTableAsync.", tableName);
                    }
                    else 
                    { 
                        _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] Table exists in sqlite_master.", tableName); 
                    }
                }
                _tableExistsCache[tableName] = true;
                _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] Marked in cache as existing.", tableName);
            }
            finally 
            { 
                if (disposeContext && contextToUse != null) 
                { 
                    _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] Disposing temporary DbContext.", tableName);
                    await contextToUse.DisposeAsync(); 
                    _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] Temporary DbContext disposed.", tableName);
                }
            }
        }
        catch (Exception ex) 
        { 
            _logger.Error(ex, "EnsureMonthlyTableExistsInternal: [{Table}] Error ensuring table exists.", tableName); 
            _tableExistsCache.TryRemove(tableName, out _); 
        }
        _logger.Debug("EnsureMonthlyTableExistsInternal: [{Table}] Exiting method.", tableName);
    }

    private static async Task CreateMonthlyTableAsync(PackageHistoryDbContext dbContext, string tableName)
    {
        // 表名直接嵌入SQL字符串，不使用参数化
        var sql = $"CREATE TABLE IF NOT EXISTS \"{tableName}\" (" +
                  "\"Id\" INTEGER PRIMARY KEY AUTOINCREMENT, " +
                  "\"Index\" INTEGER NOT NULL, " +
                  "\"Barcode\" TEXT NOT NULL COLLATE NOCASE, " +
                  "\"SegmentCode\" TEXT NULL COLLATE NOCASE, " +
                  "\"Weight\" REAL NOT NULL, " +
                  "\"ChuteNumber\" INTEGER NULL, " +
                  "\"CreateTime\" TEXT NOT NULL, " +
                  "\"ErrorMessage\" TEXT NULL, " +
                  "\"Length\" REAL NULL, " +
                  "\"Width\" REAL NULL, " +
                  "\"Height\" REAL NULL, " +
                  "\"Volume\" REAL NULL, " +
                  "\"Status\" INTEGER NOT NULL, " +
                  "\"StatusDisplay\" TEXT NULL, " +
                  "\"ImagePath\" TEXT NULL, " +
                  "\"PalletName\" TEXT NULL, " +
                  "\"PalletWeight\" REAL NULL, " +
                  "\"PalletLength\" REAL NULL, " +
                  "\"PalletWidth\" REAL NULL, " +
                  "\"PalletHeight\" REAL NULL" +
                  ");";
        await dbContext.Database.ExecuteSqlRawAsync(sql);

        // 为 Barcode 和 CreateTime 创建索引以提高查询性能
        // 索引名包含表名以确保唯一性
        var indexBarcodeSql = $"CREATE INDEX IF NOT EXISTS \"IX_{tableName}_Barcode\" ON \"{tableName}\" (\"Barcode\");";
        await dbContext.Database.ExecuteSqlRawAsync(indexBarcodeSql);

        var indexCreateTimeSql = $"CREATE INDEX IF NOT EXISTS \"IX_{tableName}_CreateTime\" ON \"{tableName}\" (\"CreateTime\");";
        await dbContext.Database.ExecuteSqlRawAsync(indexCreateTimeSql);

        var indexStatusSql = $"CREATE INDEX IF NOT EXISTS \"IX_{tableName}_Status\" ON \"{tableName}\" (\"Status\");";
        await dbContext.Database.ExecuteSqlRawAsync(indexStatusSql);
    }

    private async Task<bool> TableExistsAsync(PackageHistoryDbContext context, string tableName)
    {
        if (_tableExistsCache.TryGetValue(tableName, out var exists) && exists) return true;
        try
        {
            var connection = context.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            var escapedTableName = tableName.Replace("'", "''");
            var command = connection.CreateCommand();
            command.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{escapedTableName}';";
            var result = await command.ExecuteScalarAsync();
            exists = result != null && result != DBNull.Value;
            if (exists) _tableExistsCache[tableName] = true;
            else _tableExistsCache.TryRemove(tableName, out _);
            return exists;
        }
        catch (Exception ex) { _logger.Error(ex, "TableExistsAsync: Error checking if table {Table} exists.", tableName); _tableExistsCache.TryRemove(tableName, out _); return false; }
    }

    private async Task<List<string>> GetAllHistoricalTableNamesAsync(bool useSemaphore = true)
    {
        var tableNames = new List<string>();
        if (useSemaphore) await _semaphore.WaitAsync();
        try
        {
            await using var context = CreateDbContext(); 
            var connection = context.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name GLOB '{TablePrefix}[0-9][0-9][0-9][0-9][0-9][0-9]';";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tableNames.Add(reader.GetString(0));
            _logger.Debug("GetAllHistoricalTableNamesAsync ({Sema}): Found {Count} tables.", useSemaphore ? "Used" : "Not Used", tableNames.Count);
        }
        catch (Exception ex) { _logger.Error(ex, "GetAllHistoricalTableNamesAsync: Error getting table names."); }
        finally { if (useSemaphore) _semaphore.Release(); }
        return tableNames;
    }

    private bool TryParseDateFromTableName(string tableName, out DateTime date)
    {
        date = DateTime.MinValue;
        if (string.IsNullOrEmpty(tableName) || !tableName.StartsWith(TablePrefix))
        { _logger.Verbose("TryParseDateFromTableName: Invalid name (null/prefix) {Table}", tableName); return false; }
        var dateString = tableName[TablePrefix.Length..];
        if (dateString.Length == 6)
            return DateTime.TryParseExact(dateString, "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None,
                out date);
        _logger.Verbose("TryParseDateFromTableName: Invalid date part length {Table} -> {DateS}", tableName, dateString); return false;
    }

    public async Task MigrateLegacyDataIfNeededAsync()
    {
        _logger.Information("MigrateLegacyDataIfNeededAsync: 开始检查从旧数据库文件 packages.db 进行数据迁移...");
        string legacyDbFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "packages.db"); // 旧数据库文件名 (小写)

        if (!File.Exists(legacyDbFilePath))
        {
            _logger.Information("MigrateLegacyDataIfNeededAsync: 未找到旧数据库文件 {LegacyDbFile}，无需迁移。", legacyDbFilePath);
            return;
        }

        _logger.Information("MigrateLegacyDataIfNeededAsync: 发现旧数据库文件 {LegacyDbFile}，准备迁移。", legacyDbFilePath);

        var legacyDbConnectionString = $"Data Source={legacyDbFilePath}";
        List<string> legacyTableNamesInOldDb;
        try
        {
            legacyTableNamesInOldDb = await GetLegacyTableNamesFromSpecificDbAsync(legacyDbConnectionString);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "MigrateLegacyDataIfNeededAsync: 访问旧数据库文件 {LegacyDbFile} 获取表列表时出错。取消迁移。", legacyDbFilePath);
            return; 
        }

        if (!legacyTableNamesInOldDb.Any())
        {
            _logger.Information("MigrateLegacyDataIfNeededAsync: 旧数据库文件 {LegacyDbFile} 中未找到符合 '{LegacyTablePrefix}yyyyMM' 格式的表。", legacyDbFilePath, LegacyTablePrefix);
            RenameMigratedLegacyDbFile(legacyDbFilePath, "_empty_or_no_legacy_tables");
            return;
        }

        _logger.Information("MigrateLegacyDataIfNeededAsync: 在 {LegacyDbFile} 中发现 {Count} 个旧格式表: {Tables}", legacyDbFilePath, legacyTableNamesInOldDb.Count, string.Join(", ", legacyTableNamesInOldDb));

        bool overallMigrationSuccess = true;
        int totalMigratedRecordsAcrossAllTables = 0;

        foreach (var legacyTableName in legacyTableNamesInOldDb) 
        {
            _logger.Information("MigrateLegacyDataIfNeededAsync: 正在处理旧库 {LegacyDbFile} 中的表 {LegacyTable}", legacyDbFilePath, legacyTableName);
            if (!TryParseDateFromLegacyTableName(legacyTableName, out var tableDate)) 
            {
                _logger.Warning("MigrateLegacyDataIfNeededAsync: 无法从旧表名 {LegacyTable} 解析日期，跳过此表迁移。", legacyTableName);
                overallMigrationSuccess = false; 
                continue;
            }

            var newTableNameInNewDb = GetTableName(tableDate); 
            await EnsureMonthlyTableExistsInternal(tableDate); 

            var recordsToMigrate = new List<PackageHistoryRecord>();
            try
            {
                await using var legacyConnection = new SqliteConnection(legacyDbConnectionString);
                await legacyConnection.OpenAsync();

                var querySql = $"SELECT Id, PackageIndex, Barcode, SegmentCode, Weight, ChuteNumber, Status, StatusDisplay, CreateTime, Length, Width, Height, Volume, ErrorMessage, ImagePath, PalletName, PalletWeight, PalletLength, PalletWidth, PalletHeight FROM `{legacyTableName.Replace("`", "``")}`;";
                
                await using var command = legacyConnection.CreateCommand();
                command.CommandText = querySql;
                await using var reader = await command.ExecuteReaderAsync();
                
                int rowCount = 0;
                while (await reader.ReadAsync())
                {
                    rowCount++;
                    var record = new PackageHistoryRecord
                    {
                        Index = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                        Barcode = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        SegmentCode = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Weight = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                        ChuteNumber = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                        Status = reader.IsDBNull(6) ? Common.Models.Package.PackageStatus.Error : (Common.Models.Package.PackageStatus)reader.GetInt32(6),
                        StatusDisplay = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                        Length = reader.IsDBNull(9) ? null : reader.GetDouble(9),
                        Width = reader.IsDBNull(10) ? null : reader.GetDouble(10),
                        Height = reader.IsDBNull(11) ? null : reader.GetDouble(11),
                        Volume = reader.IsDBNull(12) ? null : reader.GetDouble(12),
                        ErrorMessage = reader.IsDBNull(13) ? null : reader.GetString(13),
                        ImagePath = reader.IsDBNull(14) ? null : reader.GetString(14),
                        PalletName = reader.IsDBNull(15) ? null : reader.GetString(15),
                        PalletWeight = reader.IsDBNull(16) ? null : reader.GetDouble(16),
                        PalletLength = reader.IsDBNull(17) ? null : reader.GetDouble(17),
                        PalletWidth = reader.IsDBNull(18) ? null : reader.GetDouble(18),
                        PalletHeight = reader.IsDBNull(19) ? null : reader.GetDouble(19)
                    };

                    var createTimeString = reader.IsDBNull(8) ? null : reader.GetString(8);
                    if (DateTime.TryParse(createTimeString, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var createTime) || 
                        DateTime.TryParse(createTimeString, CultureInfo.CurrentCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out createTime) ||
                        DateTime.TryParseExact(createTimeString, "yyyy-MM-dd HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out createTime) ||
                        DateTime.TryParseExact(createTimeString, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out createTime) ||
                        DateTime.TryParseExact(createTimeString, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out createTime)
                       )
                    {
                        record.CreateTime = createTime.ToUniversalTime(); 
                    }
                    else
                    {
                        _logger.Warning("MigrateLegacyDataIfNeededAsync: 无法解析旧表 {LegacyTable} 条码 {Barcode} 的 CreateTime '{CtStr}'，使用 DateTime.MinValue(UTC)", legacyTableName, record.Barcode, createTimeString);
                        record.CreateTime = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
                    }
                    recordsToMigrate.Add(record);
                }
                _logger.Information("MigrateLegacyDataIfNeededAsync: 从旧库 {LegacyDbFile} 的表 {LegacyTable} 读取了 {Count} 条记录。", legacyDbFilePath, legacyTableName, rowCount);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "MigrateLegacyDataIfNeededAsync: 从旧库 {LegacyDbFile} 的表 {LegacyTable} 读取数据时出错。", legacyDbFilePath, legacyTableName);
                overallMigrationSuccess = false; 
                continue; 
            }

            if (recordsToMigrate.Any())
            {
                _logger.Information("MigrateLegacyDataIfNeededAsync: 开始将 {Count} 条记录从旧表 {LegacyTable} (源: {LegacyDbFile}) 迁移到新库 {NewDbFile} 的表 {NewTable}...", 
                    recordsToMigrate.Count, legacyTableName, legacyDbFilePath, _dbPath, newTableNameInNewDb);
                
                var migratedCountForCurrentTable = 0;
                await using var targetDbContext = CreateDbContext(tableDate); 
                
                foreach (var batch in recordsToMigrate.Chunk(1000))
                {
                    var newRecordsInBatch = new List<PackageHistoryRecord>();
                    foreach(var record in batch)
                    {
                        var existingInNewTable = await targetDbContext.Set<PackageHistoryRecord>().AsNoTracking()
                            .FirstOrDefaultAsync(r => r.Barcode == record.Barcode && r.CreateTime == record.CreateTime);
                        if (existingInNewTable == null)
                        {
                            newRecordsInBatch.Add(record);
                        }
                        else
                        {
                            _logger.Debug("MigrateLegacyDataIfNeededAsync: 记录 Barcode={Barcode}, CreateTime={CreateTime} 已存在于新库表 {NewTable}, 跳过迁移。", record.Barcode, record.CreateTime, newTableNameInNewDb);
                        }
                    }
                    if (newRecordsInBatch.Any())
                    {
                        targetDbContext.AddRange(newRecordsInBatch);
                        await targetDbContext.SaveChangesAsync();
                        migratedCountForCurrentTable += newRecordsInBatch.Count;
                    }
                }
                totalMigratedRecordsAcrossAllTables += migratedCountForCurrentTable;
                _logger.Information("MigrateLegacyDataIfNeededAsync: 从旧表 {LegacyTable} 到新库表 {NewTable} 共迁移了 {MigratedCount} 条记录 (源表总共 {TotalInOldTable} 条)。", 
                    legacyTableName, newTableNameInNewDb, migratedCountForCurrentTable, recordsToMigrate.Count);
            }
            else
            {
                _logger.Information("MigrateLegacyDataIfNeededAsync: 旧库表 {LegacyTable} 中没有需要迁移的记录。", legacyTableName);
            }
        }

        // 在重命名旧数据库文件之前，尝试清理所有SQLite连接池，以确保文件句柄被释放
        SqliteConnection.ClearAllPools();
        _logger.Debug("MigrateLegacyDataIfNeededAsync: Called SqliteConnection.ClearAllPools() before renaming legacy DB file.");

        if (overallMigrationSuccess && legacyTableNamesInOldDb.Any())
        {
            RenameMigratedLegacyDbFile(legacyDbFilePath);
        }
        else if (!legacyTableNamesInOldDb.Any() && File.Exists(legacyDbFilePath)) 
        {
             _logger.Information("MigrateLegacyDataIfNeededAsync: 旧数据库文件 {LegacyDbFile} 不包含有效数据或表，将进行重命名。", legacyDbFilePath);
             RenameMigratedLegacyDbFile(legacyDbFilePath, "_no_relevant_data");
        }
        else if (!overallMigrationSuccess)
        {
            _logger.Warning("MigrateLegacyDataIfNeededAsync: 由于在迁移过程中发生错误，旧数据库文件 {LegacyDbFile} 未被自动重命名。请手动检查。", legacyDbFilePath);
        }
        _logger.Information("MigrateLegacyDataIfNeededAsync: 旧数据库文件迁移过程完成。总共迁移了 {TotalMigrated} 条记录。", totalMigratedRecordsAcrossAllTables);
    }

    private async Task<List<string>> GetLegacyTableNamesFromSpecificDbAsync(string connectionString)
    {
        var tableNames = new List<string>();
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name GLOB '{LegacyTablePrefix}[0-9][0-9][0-9][0-9][0-9][0-9]';";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tableNames.Add(reader.GetString(0));
            }
            _logger.Debug("GetLegacyTableNamesFromSpecificDbAsync: Found {Count} legacy tables in specific DB.", tableNames.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GetLegacyTableNamesFromSpecificDbAsync: Error getting legacy table names from specific DB ({ConnStr}).", connectionString);
            throw; // Rethrow to allow MigrateLegacyDataIfNeededAsync to handle cancellation of migration.
        }
        return tableNames;
    }
    
    private bool TryParseDateFromLegacyTableName(string tableName, out DateTime date)
    {
        date = DateTime.MinValue;
        if (string.IsNullOrEmpty(tableName) || !tableName.StartsWith(LegacyTablePrefix))
        { _logger.Verbose("TryParseDateFromLegacyTableName: Invalid legacy table name (null/prefix) {Table}", tableName); return false; }
        var dateString = tableName.Substring(LegacyTablePrefix.Length);
        if (dateString.Length == 6)
        {
            if (DateTime.TryParseExact(dateString, "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                return true;
            }
        }
        _logger.Verbose("TryParseDateFromLegacyTableName: Invalid date part length for legacy table {Table} -> {DateS}", tableName, dateString); return false;
    }

    private void RenameMigratedLegacyDbFile(string legacyDbFilePath, string suffixDetail = "")
    {
        try
        {
            string directory = Path.GetDirectoryName(legacyDbFilePath) ?? AppDomain.CurrentDomain.BaseDirectory; // Fallback for directory
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(legacyDbFilePath);
            string extension = Path.GetExtension(legacyDbFilePath); // .db
            string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            string newLegacyDbFileName = $"{fileNameWithoutExt}{suffixDetail}_migrated_{timestamp}{extension}";
            string newFullPath = Path.Combine(directory, newLegacyDbFileName);
            
            File.Move(legacyDbFilePath, newFullPath);
            _logger.Information("RenameMigratedLegacyDbFile: 旧数据库文件 {LegacyDbFile} 已成功重命名为 {NewName}。", legacyDbFilePath, newFullPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "RenameMigratedLegacyDbFile: 重命名旧数据库文件 {LegacyDbFile} 失败。请手动检查。", legacyDbFilePath);
        }
    }
} 
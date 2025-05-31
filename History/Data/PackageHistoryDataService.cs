using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Serilog;

namespace History.Data;

/// <summary>
///     包裹历史数据服务实现
/// </summary>
internal class PackageHistoryDataService : IPackageHistoryDataService
{
    private readonly string _dbPath;
    private readonly DbContextOptions<PackageHistoryDbContext> _options;
    private readonly ConcurrentDictionary<string, bool> _tableExistsCache = new();
    private const string TablePrefix = "Package_"; // 表名前缀
    private const string LegacyTablePrefix = "Packages_"; // 旧表名前缀
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    ///     构造函数
    /// </summary>
    public PackageHistoryDataService(DbContextOptions<PackageHistoryDbContext> options)
    {
        // 数据库文件将存储在应用程序根目录下的 Data/History 子目录中
        // 例如：C:\YourApp\Data\History\PackageHistory.db
        var dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        if (!Directory.Exists(dataDirectory))
        {
            try { Directory.CreateDirectory(dataDirectory); }
            catch (Exception)
            {
                // ignored
            }
        }
        _dbPath = Path.Combine(dataDirectory, "Package.db");
        _options = options;
    }

    private static string GetTableName(DateTime date)
    {
        return $"{TablePrefix}{date:yyyyMM}";
    }

    private PackageHistoryDbContext CreateDbContext(DateTime? forDate = null)
    {
        return new PackageHistoryDbContext(_options, forDate);
    }

    public async Task InitializeAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            await using var context = CreateDbContext();
            await context.Database.EnsureCreatedAsync();
            var today = DateTime.Today;
            await EnsureMonthlyTableExistsInternal(today, context);
            await EnsureMonthlyTableExistsInternal(today.AddMonths(1), context);
             // 在初始化末尾执行数据迁移检查
            await MigrateLegacyDataIfNeededAsync();
        }
        catch (Exception)
        {
            // ignored
        }
        finally { _semaphore.Release(); }
    }

    public async Task AddPackageAsync(PackageHistoryRecord record)
    {
        try
        {
            if (record.CreateTime == DateTime.MinValue)
            { record.CreateTime = DateTime.Now; }
            else if (record.CreateTime > DateTime.Now.AddHours(1) || record.CreateTime < DateTime.Now.AddYears(-10))
            { record.CreateTime = DateTime.Now; }

            GetTableName(record.CreateTime);
            await EnsureMonthlyTableExistsInternal(record.CreateTime);
            await using var dbContext = CreateDbContext(record.CreateTime);
            var existingRecord = await dbContext.Set<PackageHistoryRecord>().AsNoTracking().FirstOrDefaultAsync(r => r.Barcode == record.Barcode && r.CreateTime == record.CreateTime);
            if (existingRecord != null)
            { return; }
            dbContext.Add(record);
            await dbContext.SaveChangesAsync();
        }
        catch (Exception)
        {
            // ignored
        }
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
            return null;
        }
        catch (Exception) { return null; }
    }

    public async Task<(IEnumerable<PackageHistoryRecord> Records, int TotalCount)> GetPackagesAsync(DateTime? startDate, DateTime? endDate, string? barcodeFilter, int pageNumber, int pageSize)
    {
        if (pageNumber <= 0) pageNumber = 1;
        if (pageSize <= 0) pageSize = 1000;
        
        var allMatchingRecords = new List<PackageHistoryRecord>();
        var relevantTableNames = GetTableNamesForDateRange(startDate, endDate);

        if (relevantTableNames.Count == 0)
        { 
            return ([], 0); 
        }

        try
        {
            foreach (var tableName in relevantTableNames)
            {
                if (!TryParseDateFromTableName(tableName, out var tableDate)) 
                { 
                    continue; 
                }

                await using var context = CreateDbContext(tableDate);
                if (!await TableExistsAsync(context, tableName)) 
                { 
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
                catch (Exception)
                {
                    // ignored
                }
            }
        }
        catch (Exception) 
        { 
            return ([], 0); 
        }
        
        var sortedRecordsList = allMatchingRecords.OrderByDescending(p => p.CreateTime).ToList();

        var totalCount = sortedRecordsList.Count;
        var pagedRecords = sortedRecordsList.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();
        
        return (pagedRecords, totalCount);
    }

    public async Task UpdatePackageAsync(PackageHistoryRecord package)
    {
        if (package.Id == 0) { return; }
        
        var tableName = GetTableName(package.CreateTime);
        await _semaphore.WaitAsync();
        try
        {
            await EnsureMonthlyTableExistsInternal(package.CreateTime);
            await using var context = CreateDbContext(package.CreateTime);

            if (!await TableExistsAsync(context, tableName)) 
            { 
                return; 
            }
            
            var existingRecord = await context.Set<PackageHistoryRecord>().FirstOrDefaultAsync(r => r.Id == package.Id);
            if (existingRecord != null)
            { 
                context.Entry(existingRecord).CurrentValues.SetValues(package); 
                await context.SaveChangesAsync(); 
            }
        }
        catch (Exception)
        {
            // ignored
        }
        finally { _semaphore.Release(); }
    }

    public async Task DeletePackageAsync(long id, DateTime createTime)
    {
        if (id == 0) { return; }
        
        var tableName = GetTableName(createTime);
        await _semaphore.WaitAsync();
        try
        {
            await EnsureMonthlyTableExistsInternal(createTime);
            await using var context = CreateDbContext(createTime);

            if (!await TableExistsAsync(context, tableName)) 
            { 
                return; 
            }
            
            var recordToDelete = await context.Set<PackageHistoryRecord>().FirstOrDefaultAsync(r => r.Id == id); 
            if (recordToDelete != null)
            { 
                context.Remove(recordToDelete); 
                await context.SaveChangesAsync(); 
            }
        }
        catch (Exception)
        {
            // ignored
        }
        finally { _semaphore.Release(); }
    }

    public async Task RepairDatabaseAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            var allKnownTables = await GetAllHistoricalTableNamesAsync(useSemaphore: false);
            if (allKnownTables.Count == 0) 
            { 
                await EnsureMonthlyTableExistsInternal(DateTime.Today);
                return; 
            }

            foreach (var tableName in allKnownTables)
            { 
                if (TryParseDateFromTableName(tableName, out var tableDate)) 
                { 
                    await EnsureMonthlyTableExistsInternal(tableDate);
                }
            }
        }
        catch (Exception)
        {
            // ignored
        }
        finally { _semaphore.Release(); }
    }

    public async Task CreateTableForMonthAsync(DateTime date)
    {
        await EnsureMonthlyTableExistsInternal(date);
    }

    public async Task CleanupOldTablesAsync(int monthsToKeep)
    {
        if (monthsToKeep <= 0) { return; }
        
        await _semaphore.WaitAsync();
        try
        {
            var cutoffDate = DateTime.Today.AddMonths(-monthsToKeep);
            var allTables = await GetAllHistoricalTableNamesAsync(useSemaphore: false);

            if (allTables.Count == 0) { return; }

            foreach (var tableName in allTables)
            {
                if (!TryParseDateFromTableName(tableName, out var tableDate)) continue;
                if (tableDate >= new DateTime(cutoffDate.Year, cutoffDate.Month, 1)) continue;
                try 
                { 
                    await using var context = CreateDbContext(); 
                    var esc = tableName.Replace("`", "``"); 
                    await context.Database.ExecuteSqlAsync($"DROP TABLE IF EXISTS `{esc}`"); 
                    _tableExistsCache.TryRemove(tableName, out _);
                }
                catch (Exception)
                {
                    // ignored
                }
            }
        }
        catch (Exception)
        {
            // ignored
        }
        finally { _semaphore.Release(); }
    }

    // ---- 以下为私有辅助方法 ----

    private static List<string> GetTableNamesForDateRange(DateTime? startDate, DateTime? endDate)
    {
        var tableNames = new HashSet<string>();
        var today = DateTime.Today;
        var effectiveStartDate = startDate ?? new DateTime(today.Year, today.Month, 1);
        var effectiveEndDate = endDate ?? new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        if (effectiveEndDate < effectiveStartDate) { effectiveEndDate = effectiveStartDate; }
        var currentMonthIterator = new DateTime(effectiveStartDate.Year, effectiveStartDate.Month, 1);
        while (currentMonthIterator <= effectiveEndDate)
        { tableNames.Add(GetTableName(currentMonthIterator)); var nextMonth = currentMonthIterator.AddMonths(1); if (nextMonth <= currentMonthIterator) { break; } currentMonthIterator = nextMonth; }
        return [.. tableNames];
    }

    private async Task EnsureMonthlyTableExistsInternal(DateTime date, PackageHistoryDbContext? existingContext = null)
    {
        var tableName = GetTableName(date); 
        if (_tableExistsCache.TryGetValue(tableName, out var exists) && exists)
        {
            return;
        }

        try
        {
            if (_tableExistsCache.TryGetValue(tableName, out exists) && exists)
            {
                 return;
            }

            PackageHistoryDbContext? contextToUse;
            bool disposeContext = false;

            if (existingContext != null && existingContext.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
            {
                contextToUse = existingContext;
            }
            else
            {
                contextToUse = CreateDbContext(date); 
                disposeContext = true;
            }

            try
            {
                await contextToUse.Database.EnsureCreatedAsync(); 

                var connection = contextToUse.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                {
                    await connection.OpenAsync();
                }
                
                var escapedTableName = tableName.Replace("'", "''"); 
                string checkTableSql = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{escapedTableName}';";
                
                await using (var command = connection.CreateCommand()) 
                { 
                    command.CommandText = checkTableSql;
                    var result = await command.ExecuteScalarAsync(); 
                    
                    if (result == null || result == DBNull.Value) 
                    { 
                        await CreateMonthlyTableAsync(contextToUse, tableName); 
                    }
                }
                _tableExistsCache[tableName] = true;
            }
            finally 
            { 
                if (disposeContext) 
                { 
                    await contextToUse.DisposeAsync(); 
                }
            }
        }
        catch (Exception) 
        { 
            _tableExistsCache.TryRemove(tableName, out _); 
        }
    }

    private static async Task CreateMonthlyTableAsync(PackageHistoryDbContext dbContext, string tableName)
    {
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
                  "\"Status\" TEXT NULL, " +
                  "\"StatusDisplay\" TEXT NULL, " +
                  "\"ImagePath\" TEXT NULL, " +
                  "\"PalletName\" TEXT NULL, " +
                  "\"PalletWeight\" REAL NULL, " +
                  "\"PalletLength\" REAL NULL, " +
                  "\"PalletWidth\" REAL NULL, " +
                  "\"PalletHeight\" REAL NULL" +
                  ");";
        await dbContext.Database.ExecuteSqlRawAsync(sql);

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
        catch (Exception) { _tableExistsCache.TryRemove(tableName, out _); return false; }
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
        }
        catch (Exception)
        {
            // ignored
        }
        finally { if (useSemaphore) _semaphore.Release(); }
        return tableNames;
    }

    private static bool TryParseDateFromTableName(string tableName, out DateTime date)
    {
        date = DateTime.MinValue;
        if (string.IsNullOrEmpty(tableName) || !tableName.StartsWith(TablePrefix))
        { return false; }
        var dateString = tableName[TablePrefix.Length..];
        if (dateString.Length == 6)
            return DateTime.TryParseExact(dateString, "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None,
                out date);
        return false;
    }

    public async Task MigrateLegacyDataIfNeededAsync()
    {
        string legacyDbFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "packages.db"); // 旧数据库文件名 (小写)

        if (!File.Exists(legacyDbFilePath))
        {
            return;
        }

        var legacyDbConnectionString = $"Data Source={legacyDbFilePath}";
        List<string> legacyTableNamesInOldDb;
        try
        {
            legacyTableNamesInOldDb = await GetLegacyTableNamesFromSpecificDbAsync(legacyDbConnectionString);
        }
        catch (Exception)
        {
            return; 
        }

        if (legacyTableNamesInOldDb.Count == 0)
        {
            RenameMigratedLegacyDbFile(legacyDbFilePath, "_empty_or_no_legacy_tables");
            return;
        }

        Log.Information("MigrateLegacyDataIfNeededAsync: 在 {LegacyDbFile} 中发现 {Count} 个旧格式表: {Tables}", legacyDbFilePath, legacyTableNamesInOldDb.Count, string.Join(", ", legacyTableNamesInOldDb));

        bool overallMigrationSuccess = true;
        int totalMigratedRecordsAcrossAllTables = 0;

        foreach (var legacyTableName in legacyTableNamesInOldDb) 
        {
            if (!TryParseDateFromLegacyTableName(legacyTableName, out var tableDate)) 
            {
                Log.Warning("MigrateLegacyDataIfNeededAsync: 无法从旧表名 {LegacyTable} 解析日期，跳过此表迁移。", legacyTableName);
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
                        Log.Warning("MigrateLegacyDataIfNeededAsync: 无法解析旧表 {LegacyTable} 条码 {Barcode} 的 CreateTime '{CtStr}'，使用 DateTime.MinValue(UTC)", legacyTableName, record.Barcode, createTimeString);
                        record.CreateTime = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
                    }
                    
                    if (!reader.IsDBNull(7) && !string.IsNullOrEmpty(reader.GetString(7)))
                    {
                        record.Status = reader.GetString(7);
                    }
                    else if (!reader.IsDBNull(6))
                    {
                        record.Status = reader.GetInt32(6).ToString();
                    }
                    else
                    {
                        record.Status = "Error";
                    }

                    recordsToMigrate.Add(record);
                }
                Log.Information("MigrateLegacyDataIfNeededAsync: 从旧库 {LegacyDbFile} 的表 {LegacyTable} 读取了 {Count} 条记录。", legacyDbFilePath, legacyTableName, rowCount);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "MigrateLegacyDataIfNeededAsync: 从旧库 {LegacyDbFile} 的表 {LegacyTable} 读取数据时出错。", legacyDbFilePath, legacyTableName);
                overallMigrationSuccess = false; 
                continue; 
            }

            if (recordsToMigrate.Count != 0)
            {
                Log.Information("MigrateLegacyDataIfNeededAsync: 开始将 {Count} 条记录从旧表 {LegacyTable} (源: {LegacyDbFile}) 迁移到新库 {NewDbFile} 的表 {NewTable}...", 
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
                            Log.Debug("MigrateLegacyDataIfNeededAsync: 记录 Barcode={Barcode}, CreateTime={CreateTime} 已存在于新库表 {NewTable}, 跳过迁移。", record.Barcode, record.CreateTime, newTableNameInNewDb);
                        }
                    }
                    if (newRecordsInBatch.Count != 0)
                    {
                        targetDbContext.AddRange(newRecordsInBatch);
                        await targetDbContext.SaveChangesAsync();
                        migratedCountForCurrentTable += newRecordsInBatch.Count;
                    }
                }
                totalMigratedRecordsAcrossAllTables += migratedCountForCurrentTable;
                Log.Information("MigrateLegacyDataIfNeededAsync: 从旧表 {LegacyTable} 到新库表 {NewTable} 共迁移了 {MigratedCount} 条记录 (源表总共 {TotalInOldTable} 条)。", 
                    legacyTableName, newTableNameInNewDb, migratedCountForCurrentTable, recordsToMigrate.Count);
            }
            else
            {
                Log.Information("MigrateLegacyDataIfNeededAsync: 旧库表 {LegacyTable} 中没有需要迁移的记录。", legacyTableName);
            }
        }

        // 在重命名旧数据库文件之前，尝试清理所有SQLite连接池，以确保文件句柄被释放
        SqliteConnection.ClearAllPools();
        Log.Debug("MigrateLegacyDataIfNeededAsync: Called SqliteConnection.ClearAllPools() before renaming legacy DB file.");

        if (overallMigrationSuccess && legacyTableNamesInOldDb.Count != 0)
        {
            RenameMigratedLegacyDbFile(legacyDbFilePath);
        }
        else if (legacyTableNamesInOldDb.Count == 0 && File.Exists(legacyDbFilePath)) 
        {
             Log.Information("MigrateLegacyDataIfNeededAsync: 旧数据库文件 {LegacyDbFile} 不包含有效数据或表，将进行重命名。", legacyDbFilePath);
            RenameMigratedLegacyDbFile(legacyDbFilePath, "_no_relevant_data");
        }
        else if (!overallMigrationSuccess)
        {
            Log.Warning("MigrateLegacyDataIfNeededAsync: 由于在迁移过程中发生错误，旧数据库文件 {LegacyDbFile} 未被自动重命名。请手动检查。", legacyDbFilePath);
        }
        Log.Information("MigrateLegacyDataIfNeededAsync: 旧数据库文件迁移过程完成。总共迁移了 {TotalMigrated} 条记录。", totalMigratedRecordsAcrossAllTables);
    }

    private static async Task<List<string>> GetLegacyTableNamesFromSpecificDbAsync(string connectionString)
    {
        var tableNames = new List<string>();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name GLOB '{LegacyTablePrefix}[0-9][0-9][0-9][0-9][0-9][0-9]';";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tableNames.Add(reader.GetString(0));
        }

        return tableNames;
    }
    
    private static bool TryParseDateFromLegacyTableName(string tableName, out DateTime date)
    {
        date = DateTime.MinValue;
        if (string.IsNullOrEmpty(tableName) || !tableName.StartsWith(LegacyTablePrefix))
        { return false; }
        var dateString = tableName[LegacyTablePrefix.Length..];
        if (dateString.Length == 6)
        {
            if (DateTime.TryParseExact(dateString, "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                return true;
            }
        }
        return false;
    }

    private static void RenameMigratedLegacyDbFile(string legacyDbFilePath, string suffixDetail = "")
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
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RenameMigratedLegacyDbFile: 重命名旧数据库文件 {LegacyDbFile} 失败。请手动检查。", legacyDbFilePath);
        }
    }
} 
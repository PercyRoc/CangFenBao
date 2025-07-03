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
        Log.Information("PackageHistoryDataService: 调用 AddPackageAsync. 当前系统时间: {SystemTime:yyyy-MM-dd HH:mm:ss.fff}. 初始记录创建时间: {RecordCreateTime:yyyy-MM-dd HH:mm:ss.fff}", DateTime.Now, record.CreateTime);
        try
        {
            // 根据用户要求，只使用 record.CreateTime 的原始值来确定表名，不再进行时间校验和修正。
            // 移除以下被注释掉的逻辑，以确保 CreateTime 不会被修改：
            // if (record.CreateTime == DateTime.MinValue)
            // {
            //     Log.Warning("PackageHistoryDataService: 记录创建时间为 DateTime.MinValue, 设置为当前系统时间.");
            //     record.CreateTime = DateTime.Now;
            // }
            // else if (record.CreateTime > DateTime.Now.AddHours(1) || record.CreateTime < DateTime.Now.AddYears(-10))
            // {
            //     Log.Warning("PackageHistoryDataService: 记录创建时间 {RecordCreateTime:yyyy-MM-dd HH:mm:ss.fff} 被认为无效 (过远未来或过去), 设置为当前系统时间 ({SystemTime:yyyy-MM-dd HH:mm:ss.fff}).", record.CreateTime, DateTime.Now);
            //     record.CreateTime = DateTime.Now;
            // }
            Log.Information("PackageHistoryDataService: 最终用于确定表的记录创建时间: {FinalRecordCreateTime:yyyyMM} - {FinalRecordCreateTime:yyyy-MM-dd HH:mm:ss.fff}", record.CreateTime, record.CreateTime);

            var targetTableName = GetTableName(record.CreateTime);
            Log.Information("PackageHistoryDataService: AddPackageAsync: 条码 {Barcode} (创建时间 {CreateTime:yyyy-MM-dd HH:mm:ss.fff}) 的数据将被写入表 {TargetTableName}.", record.Barcode, record.CreateTime, targetTableName);

            await EnsureMonthlyTableExistsInternal(record.CreateTime);
            
            // 使用原生 SQL 进行插入操作
            await using var connection = new SqliteConnection(_options.FindExtension<Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal.SqliteOptionsExtension>()?.ConnectionString);
            await connection.OpenAsync();

            // 【性能优化】使用乐观并发：直接尝试插入，利用唯一索引约束防重复
            string insertSql = $"INSERT INTO \"{targetTableName}\" (" +
                               "\"Index\", Barcode, SegmentCode, Weight, ChuteNumber, Status, StatusDisplay, CreateTime, ErrorMessage, Length, Width, Height, Volume, ImagePath, PalletName, PalletWeight, PalletLength, PalletWidth, PalletHeight) VALUES (" +
                               "@Index, @Barcode, @SegmentCode, @Weight, @ChuteNumber, @Status, @StatusDisplay, @CreateTime, @ErrorMessage, @Length, @Width, @Height, @Volume, @ImagePath, @PalletName, @PalletWeight, @PalletLength, @PalletWidth, @PalletHeight)";

            try
            {
                await using var insertCommand = connection.CreateCommand();
                insertCommand.CommandText = insertSql;
                insertCommand.Parameters.Add(new SqliteParameter("@Index", record.Index));
                insertCommand.Parameters.Add(new SqliteParameter("@Barcode", record.Barcode));
                insertCommand.Parameters.Add(new SqliteParameter("@SegmentCode", (object?)record.SegmentCode ?? DBNull.Value));
                insertCommand.Parameters.Add(new SqliteParameter("@Weight", record.Weight));
                insertCommand.Parameters.Add(new SqliteParameter("@ChuteNumber", (object?)record.ChuteNumber ?? DBNull.Value));
                insertCommand.Parameters.Add(new SqliteParameter("@Status", (object?)record.Status ?? DBNull.Value));
                insertCommand.Parameters.Add(new SqliteParameter("@StatusDisplay", record.StatusDisplay));
                insertCommand.Parameters.Add(new SqliteParameter("@CreateTime", record.CreateTime.ToString("yyyy-MM-dd HH:mm:ss.fff")));
                insertCommand.Parameters.Add(new SqliteParameter("@ErrorMessage", (object?)record.ErrorMessage ?? DBNull.Value));
                insertCommand.Parameters.Add(new SqliteParameter("@Length", (object?)record.Length ?? DBNull.Value));
                insertCommand.Parameters.Add(new SqliteParameter("@Width", (object?)record.Width ?? DBNull.Value));
                insertCommand.Parameters.Add(new SqliteParameter("@Height", (object?)record.Height ?? DBNull.Value));
                insertCommand.Parameters.Add(new SqliteParameter("@Volume", (object?)record.Volume ?? DBNull.Value));
                insertCommand.Parameters.Add(new SqliteParameter("@ImagePath", (object?)record.ImagePath ?? DBNull.Value));
                insertCommand.Parameters.Add(new SqliteParameter("@PalletName", (object?)record.PalletName ?? DBNull.Value));
                insertCommand.Parameters.Add(new SqliteParameter("@PalletWeight", (object?)record.PalletWeight ?? DBNull.Value));
                insertCommand.Parameters.Add(new SqliteParameter("@PalletLength", (object?)record.PalletLength ?? DBNull.Value));
                insertCommand.Parameters.Add(new SqliteParameter("@PalletWidth", (object?)record.PalletWidth ?? DBNull.Value));
                insertCommand.Parameters.Add(new SqliteParameter("@PalletHeight", (object?)record.PalletHeight ?? DBNull.Value));

                await insertCommand.ExecuteNonQueryAsync();
                Log.Information("PackageHistoryDataService: 成功将条码 {Barcode} (创建时间 {CreateTime:yyyy-MM-dd HH:mm:ss.fff}) 的数据插入到表 {TargetTableName}.", record.Barcode, record.CreateTime, targetTableName);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT (约束冲突)
            {
                // 这是预期的错误，说明记录已经存在（被唯一索引阻止）
                Log.Information("PackageHistoryDataService: AddPackageAsync: 记录 (Barcode: {Barcode}, CreateTime: {CreateTime:yyyy-MM-dd HH:mm:ss.fff}) 已存在 (约束冲突)，跳过插入.", record.Barcode, record.CreateTime);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PackageHistoryDataService: 添加包裹历史记录时出错。");
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
        
        var allMatchingRecords = new Dictionary<DateTime, PackageHistoryRecord>();
        var relevantTableNames = GetTableNamesForDateRange(startDate, endDate);
        Log.Information("PackageHistoryDataService: 查询时间范围 {StartDate} - {EndDate} 对应的数据表: {TableNames}", startDate, endDate, string.Join(", ", relevantTableNames));

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
                    Log.Warning("PackageHistoryDataService: 无法从表名 {TableName} 解析日期，跳过此表查询。", tableName);
                    continue; 
                }

                // 现在直接使用 SqliteConnection 和 SqliteCommand
                await using var connection = new SqliteConnection(_options.FindExtension<Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal.SqliteOptionsExtension>()?.ConnectionString);
                await connection.OpenAsync();

                var tableExists = await TableExistsAsync(null, tableName, connection); // 传递连接，不再创建新的 DbContext
                Log.Information("PackageHistoryDataService: 检查表 {TableName} 是否存在: {TableExists}", tableName, tableExists);
                if (!tableExists) 
                { 
                    continue; 
                }
                
                var sqlBuilder = new System.Text.StringBuilder($"SELECT Id, \"Index\", Barcode, SegmentCode, Weight, ChuteNumber, Status, StatusDisplay, CreateTime, ErrorMessage, Length, Width, Height, Volume, ImagePath, PalletName, PalletWeight, PalletLength, PalletWidth, PalletHeight FROM \"{tableName}\" WHERE 1=1");
                var parameters = new List<SqliteParameter>();

                if (startDate.HasValue)
                {
                    sqlBuilder.Append(" AND CreateTime >= @startDate");
                    parameters.Add(new SqliteParameter("@startDate", startDate.Value.ToString("yyyy-MM-dd HH:mm:ss.fff")));
                }
                if (endDate.HasValue) 
                {
                    var endOfDay = endDate.Value.Date.AddDays(1).AddTicks(-1);
                    sqlBuilder.Append(" AND CreateTime <= @endDate");
                    parameters.Add(new SqliteParameter("@endDate", endOfDay.ToString("yyyy-MM-dd HH:mm:ss.fff")));
                }
                if (!string.IsNullOrWhiteSpace(barcodeFilter)) 
                {
                    // 注意：SQLite 的 LIKE 默认是大小写不敏感的，但为了明确，我们仍使用 ToUpper/ToLower 进行匹配。
                    // 考虑到性能，如果确定数据库配置是大小写不敏感，可以移除 ToUpper/ToLower。
                    sqlBuilder.Append(" AND Barcode LIKE @barcodeFilter");
                    parameters.Add(new SqliteParameter("@barcodeFilter", $"%{barcodeFilter.Replace("[", "[[").Replace("_", "[_]").Replace("%", "[%]")}%" )); // Escape for LIKE
                }
                
                try 
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = sqlBuilder.ToString();
                    command.Parameters.AddRange(parameters.ToArray());
                    
                    var recordsFromTable = new List<PackageHistoryRecord>();
                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        recordsFromTable.Add(new PackageHistoryRecord
                        {
                            Id = reader.GetInt64(reader.GetOrdinal("Id")),
                            Index = reader.GetInt32(reader.GetOrdinal("Index")),
                            Barcode = reader.GetString(reader.GetOrdinal("Barcode")),
                            SegmentCode = reader.IsDBNull(reader.GetOrdinal("SegmentCode")) ? null : reader.GetString(reader.GetOrdinal("SegmentCode")),
                            Weight = reader.GetDouble(reader.GetOrdinal("Weight")),
                            ChuteNumber = reader.IsDBNull(reader.GetOrdinal("ChuteNumber")) ? null : reader.GetInt32(reader.GetOrdinal("ChuteNumber")),
                            CreateTime = reader.GetDateTime(reader.GetOrdinal("CreateTime")),
                            ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage")) ? null : reader.GetString(reader.GetOrdinal("ErrorMessage")),
                            Length = reader.IsDBNull(reader.GetOrdinal("Length")) ? null : reader.GetDouble(reader.GetOrdinal("Length")),
                            Width = reader.IsDBNull(reader.GetOrdinal("Width")) ? null : reader.GetDouble(reader.GetOrdinal("Width")),
                            Height = reader.IsDBNull(reader.GetOrdinal("Height")) ? null : reader.GetDouble(reader.GetOrdinal("Height")),
                            Volume = reader.IsDBNull(reader.GetOrdinal("Volume")) ? null : reader.GetDouble(reader.GetOrdinal("Volume")),
                            Status = reader.IsDBNull(reader.GetOrdinal("Status")) ? null : reader.GetString(reader.GetOrdinal("Status")),
                            StatusDisplay = reader.GetString(reader.GetOrdinal("StatusDisplay")),
                            ImagePath = reader.IsDBNull(reader.GetOrdinal("ImagePath")) ? null : reader.GetString(reader.GetOrdinal("ImagePath")),
                            PalletName = reader.IsDBNull(reader.GetOrdinal("PalletName")) ? null : reader.GetString(reader.GetOrdinal("PalletName")),
                            PalletWeight = reader.IsDBNull(reader.GetOrdinal("PalletWeight")) ? null : reader.GetDouble(reader.GetOrdinal("PalletWeight")),
                            PalletLength = reader.IsDBNull(reader.GetOrdinal("PalletLength")) ? null : reader.GetDouble(reader.GetOrdinal("PalletLength")),
                            PalletWidth = reader.IsDBNull(reader.GetOrdinal("PalletWidth")) ? null : reader.GetDouble(reader.GetOrdinal("PalletWidth")),
                            PalletHeight = reader.IsDBNull(reader.GetOrdinal("PalletHeight")) ? null : reader.GetDouble(reader.GetOrdinal("PalletHeight"))
                        });
                    }
                    Log.Information("PackageHistoryDataService: 从表 {TableName} 查询到 {RecordCount} 条记录 (过滤后).", tableName, recordsFromTable.Count);
                    
                    if (recordsFromTable.Count > 0)
                    {
                        Log.Information("PackageHistoryDataService: 表 {TableName} 查询结果示例 (前 {Count} 条记录的创建时间):", tableName, Math.Min(recordsFromTable.Count, 5));
                        for (int i = 0; i < Math.Min(recordsFromTable.Count, 5); i++)
                        {
                            Log.Information("  - Record {Index} CreateTime: {CreateTime:yyyy-MM-dd HH:mm:ss.fff}", i + 1, recordsFromTable[i].CreateTime);
                        }
                    }

                    foreach (var record in recordsFromTable)
                    {
                        var key = record.CreateTime;
                        if (!allMatchingRecords.ContainsKey(key))
                        {
                            allMatchingRecords[key] = record;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "PackageHistoryDataService: 从表 {TableName} 查询数据时出错。", tableName);
                    // ignored
                }
            }
        }
        catch (Exception ex) 
        { 
            Log.Error(ex, "PackageHistoryDataService: 查询包裹历史记录时发生总错误。");
            return ([], 0); 
        }
        
        var sortedRecordsList = allMatchingRecords.Values.OrderByDescending(p => p.CreateTime).ToList();

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
            
            // 使用原生 SQL 进行更新操作
            await using var connection = new SqliteConnection(_options.FindExtension<Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal.SqliteOptionsExtension>()?.ConnectionString);
            await connection.OpenAsync();

            var tableExists = await TableExistsAsync(null, tableName, connection); // 传递连接
            if (!tableExists) 
            { 
                Log.Warning("PackageHistoryDataService: UpdatePackageAsync: 目标表 {TableName} 不存在，无法更新记录 Id: {Id}.", tableName, package.Id);
                return; 
            }

            // 检查记录是否确实存在
            string checkSql = $"SELECT COUNT(*) FROM \"{tableName}\" WHERE Id = @Id AND CreateTime = @CreateTime;";
            await using var checkCommand = connection.CreateCommand();
            checkCommand.CommandText = checkSql;
            checkCommand.Parameters.Add(new SqliteParameter("@Id", package.Id));
            checkCommand.Parameters.Add(new SqliteParameter("@CreateTime", package.CreateTime.ToString("yyyy-MM-dd HH:mm:ss.fff")));
            long count = (long)await checkCommand.ExecuteScalarAsync();

            if (count == 0)
            {
                Log.Warning("PackageHistoryDataService: UpdatePackageAsync: 表 {TableName} 中未找到记录 Id: {Id} (创建时间 {CreateTime:yyyy-MM-dd HH:mm:ss.fff})，跳过更新.", tableName, package.Id, package.CreateTime);
                return; // 记录不存在，无法更新
            }
            
            string updateSql = $"UPDATE \"{tableName}\" SET " +
                               "Index = @Index, Barcode = @Barcode, SegmentCode = @SegmentCode, Weight = @Weight, ChuteNumber = @ChuteNumber, Status = @Status, StatusDisplay = @StatusDisplay, ErrorMessage = @ErrorMessage, Length = @Length, Width = @Width, Height = @Height, Volume = @Volume, ImagePath = @ImagePath, PalletName = @PalletName, PalletWeight = @PalletWeight, PalletLength = @PalletLength, PalletWidth = @PalletWidth, PalletHeight = @PalletHeight " +
                               "WHERE Id = @Id AND CreateTime = @CreateTime;";

            await using var updateCommand = connection.CreateCommand();
            updateCommand.CommandText = updateSql;
            updateCommand.Parameters.Add(new SqliteParameter("@Index", package.Index));
            updateCommand.Parameters.Add(new SqliteParameter("@Barcode", package.Barcode));
            updateCommand.Parameters.Add(new SqliteParameter("@SegmentCode", (object?)package.SegmentCode ?? DBNull.Value));
            updateCommand.Parameters.Add(new SqliteParameter("@Weight", package.Weight));
            updateCommand.Parameters.Add(new SqliteParameter("@ChuteNumber", (object?)package.ChuteNumber ?? DBNull.Value));
            updateCommand.Parameters.Add(new SqliteParameter("@Status", (object?)package.Status ?? DBNull.Value));
            updateCommand.Parameters.Add(new SqliteParameter("@StatusDisplay", package.StatusDisplay));
            updateCommand.Parameters.Add(new SqliteParameter("@ErrorMessage", (object?)package.ErrorMessage ?? DBNull.Value));
            updateCommand.Parameters.Add(new SqliteParameter("@Length", (object?)package.Length ?? DBNull.Value));
            updateCommand.Parameters.Add(new SqliteParameter("@Width", (object?)package.Width ?? DBNull.Value));
            updateCommand.Parameters.Add(new SqliteParameter("@Height", (object?)package.Height ?? DBNull.Value));
            updateCommand.Parameters.Add(new SqliteParameter("@Volume", (object?)package.Volume ?? DBNull.Value));
            updateCommand.Parameters.Add(new SqliteParameter("@ImagePath", (object?)package.ImagePath ?? DBNull.Value));
            updateCommand.Parameters.Add(new SqliteParameter("@PalletName", (object?)package.PalletName ?? DBNull.Value));
            updateCommand.Parameters.Add(new SqliteParameter("@PalletWeight", (object?)package.PalletWeight ?? DBNull.Value));
            updateCommand.Parameters.Add(new SqliteParameter("@PalletLength", (object?)package.PalletLength ?? DBNull.Value));
            updateCommand.Parameters.Add(new SqliteParameter("@PalletWidth", (object?)package.PalletWidth ?? DBNull.Value));
            updateCommand.Parameters.Add(new SqliteParameter("@PalletHeight", (object?)package.PalletHeight ?? DBNull.Value));
            // WHERE 子句参数
            updateCommand.Parameters.Add(new SqliteParameter("@Id", package.Id));
            updateCommand.Parameters.Add(new SqliteParameter("@CreateTime", package.CreateTime.ToString("yyyy-MM-dd HH:mm:ss.fff")));

            await updateCommand.ExecuteNonQueryAsync();
            Log.Information("PackageHistoryDataService: 成功更新表 {TableName} 中的记录 Id: {Id} (创建时间 {CreateTime:yyyy-MM-dd HH:mm:ss.fff}).", tableName, package.Id, package.CreateTime);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PackageHistoryDataService: 更新包裹历史记录 Id: {Id} 时出错。", package.Id);
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
            
            // 使用原生 SQL 进行删除操作
            await using var connection = new SqliteConnection(_options.FindExtension<Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal.SqliteOptionsExtension>()?.ConnectionString);
            await connection.OpenAsync();

            var tableExists = await TableExistsAsync(null, tableName, connection); // 传递连接
            if (!tableExists) 
            {
                Log.Warning("PackageHistoryDataService: DeletePackageAsync: 目标表 {TableName} 不存在，无法删除记录 Id: {Id}.", tableName, id);
                return; 
            }

            // 检查记录是否确实存在
            string checkSql = $"SELECT COUNT(*) FROM \"{tableName}\" WHERE Id = @Id AND CreateTime = @CreateTime;";
            await using var checkCommand = connection.CreateCommand();
            checkCommand.CommandText = checkSql;
            checkCommand.Parameters.Add(new SqliteParameter("@Id", id));
            checkCommand.Parameters.Add(new SqliteParameter("@CreateTime", createTime.ToString("yyyy-MM-dd HH:mm:ss.fff")));
            long count = (long)await checkCommand.ExecuteScalarAsync();

            if (count == 0)
            {
                Log.Warning("PackageHistoryDataService: DeletePackageAsync: 表 {TableName} 中未找到记录 Id: {Id} (创建时间 {CreateTime:yyyy-MM-dd HH:mm:ss.fff})，跳过删除.", tableName, id, createTime);
                return; // 记录不存在，无法删除
            }

            string deleteSql = $"DELETE FROM \"{tableName}\" WHERE Id = @Id AND CreateTime = @CreateTime;";

            await using var deleteCommand = connection.CreateCommand();
            deleteCommand.CommandText = deleteSql;
            deleteCommand.Parameters.Add(new SqliteParameter("@Id", id));
            deleteCommand.Parameters.Add(new SqliteParameter("@CreateTime", createTime.ToString("yyyy-MM-dd HH:mm:ss.fff")));

            await deleteCommand.ExecuteNonQueryAsync();
            Log.Information("PackageHistoryDataService: 成功删除表 {TableName} 中的记录 Id: {Id} (创建时间 {CreateTime:yyyy-MM-dd HH:mm:ss.fff}).", tableName, id, createTime);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PackageHistoryDataService: 删除包裹历史记录 Id: {Id} 时出错。", id);
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
            
            Log.Information("开始历史数据清理: 保留最近{MonthsToKeep}个月的数据, 截止日期: {CutoffDate:yyyy-MM-dd}, 共找到{TableCount}个历史表", 
                monthsToKeep, cutoffDate, allTables.Count);

            if (allTables.Count == 0) { return; }

            var deletedCount = 0;
            var keptCount = 0;

            foreach (var tableName in allTables)
            {
                if (!TryParseDateFromTableName(tableName, out var tableDate)) continue;
                
                if (tableDate >= new DateTime(cutoffDate.Year, cutoffDate.Month, 1))
                {
                    keptCount++;
                    Log.Debug("保留表: {TableName} (日期: {TableDate:yyyy-MM})", tableName, tableDate);
                    continue;
                }
                
                try 
                { 
                    await using var context = CreateDbContext(); 
                    var esc = tableName.Replace("`", "``"); 
                    await context.Database.ExecuteSqlAsync($"DROP TABLE IF EXISTS `{esc}`");
                    _tableExistsCache.TryRemove(tableName, out _);
                    deletedCount++;
                    Log.Information("已删除过期历史表: {TableName} (日期: {TableDate:yyyy-MM})", tableName, tableDate);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "删除历史表 {TableName} 时发生错误", tableName);
                }
            }
            
            Log.Information("历史数据清理完成: 删除了{DeletedCount}个过期表, 保留了{KeptCount}个表", deletedCount, keptCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行历史数据清理时发生错误");
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

        // 【性能优化】创建联合唯一索引，覆盖去重查询条件，效率最高
        var indexCompositeSql = $"CREATE UNIQUE INDEX IF NOT EXISTS \"IX_{tableName}_Barcode_CreateTime\" ON \"{tableName}\" (\"Barcode\", \"CreateTime\");";
        await dbContext.Database.ExecuteSqlRawAsync(indexCompositeSql);

        // 保留状态索引用于其他查询场景
        var indexStatusSql = $"CREATE INDEX IF NOT EXISTS \"IX_{tableName}_Status\" ON \"{tableName}\" (\"Status\");";
        await dbContext.Database.ExecuteSqlRawAsync(indexStatusSql);
    }

    private async Task<bool> TableExistsAsync(PackageHistoryDbContext? context, string tableName, SqliteConnection? connection = null)
    {
        if (_tableExistsCache.TryGetValue(tableName, out var exists) && exists) return true;

        SqliteConnection connToUse;
        bool disposeConnection = false;

        if (connection != null && connection.State == System.Data.ConnectionState.Open)
        {
            connToUse = connection;
        }
        else if (context != null && context.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
        {
            connToUse = (SqliteConnection)context.Database.GetDbConnection();
        }
        else
        {
            // 获取连接字符串并创建新连接
            var connectionString = _options.FindExtension<Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal.SqliteOptionsExtension>()?.ConnectionString;
            if (string.IsNullOrEmpty(connectionString))
            {
                Log.Error("PackageHistoryDataService: 无法获取数据库连接字符串。");
                _tableExistsCache.TryRemove(tableName, out _);
                return false;
            }
            connToUse = new SqliteConnection(connectionString);
            disposeConnection = true;
            await connToUse.OpenAsync();
        }

        try
        {
            var escapedTableName = tableName.Replace("'", "''");
            var command = connToUse.CreateCommand();
            command.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{escapedTableName}';";
            var result = await command.ExecuteScalarAsync();
            exists = result != null && result != DBNull.Value;
            if (exists) _tableExistsCache[tableName] = true;
            else _tableExistsCache.TryRemove(tableName, out _);
            return exists;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PackageHistoryDataService: 检查表 {TableName} 是否存在时出错。", tableName);
            _tableExistsCache.TryRemove(tableName, out _); 
            return false; 
        }
        finally
        {
            if (disposeConnection && connToUse.State == System.Data.ConnectionState.Open)
            {
                await connToUse.CloseAsync();
                await connToUse.DisposeAsync();
            }
        }
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
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Common.Models.Package;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Common.Data;

/// <summary>
///     包裹数据服务
/// </summary>
public interface IPackageDataService
{
    /// <summary>
    ///     添加包裹记录
    /// </summary>
    Task AddPackageAsync(PackageInfo package);

    /// <summary>
    ///     根据条码查询包裹
    /// </summary>
    Task<PackageRecord?> GetPackageByBarcodeAsync(string barcode, DateTime? date = null);

    /// <summary>
    ///     查询指定时间范围内的包裹
    /// </summary>
    Task<List<PackageRecord>> GetPackagesInTimeRangeAsync(DateTime startTime, DateTime endTime);

    /// <summary>
    ///     查询指定状态的包裹
    /// </summary>
    Task<List<PackageRecord>> GetPackagesByStatusAsync(PackageStatus status, DateTime? date = null);

    /// <summary>
    ///     直接获取指定日期表中的所有数据（调试用）
    /// </summary>
    Task<List<PackageRecord>> GetRawTableDataAsync(DateTime date);

    /// <summary>
    ///     获取所有表中状态为离线的包裹
    /// </summary>
    Task<List<PackageRecord>> GetAllOfflinePackagesAsync();

    /// <summary>
    ///     更新指定包裹的状态
    /// </summary>
    Task<bool> UpdatePackageStatusAsync(string barcode, PackageStatus status, string statusDisplay, DateTime? recordTime = null);
}

/// <summary>
///     包裹数据服务实现
/// </summary>
internal class PackageDataService : IPackageDataService
{
    private readonly string _dbPath;
    private readonly DbContextOptions<PackageDbContext> _options;
    private readonly ConcurrentDictionary<string, bool> _tableExistsCache = new();

    /// <summary>
    ///     构造函数
    /// </summary>
    public PackageDataService(DbContextOptions<PackageDbContext> options)
    {
        _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "packages.db");
        _options = options;

        // 初始化数据库
        InitializeDatabase().Wait();
    }

    /// <inheritdoc />
    public async Task AddPackageAsync(PackageInfo package)
    {
        try
        {
            var record = PackageRecord.FromPackageInfo(package);
            
            // 记录操作，便于调试
            Log.Debug("添加包裹记录：Barcode={Barcode}, CreateTime={CreateTime}, Status={Status}", 
                record.Barcode, record.CreateTime.ToString("yyyy-MM-dd HH:mm:ss"), record.Status);
                
            // 验证创建时间，防止未来日期导致数据错误
            if (record.CreateTime > DateTime.Now.AddHours(1)) // 允许1小时的时钟误差
            {
                Log.Warning("包裹创建时间异常 ({CreateTime})，将使用当前时间", record.CreateTime);
                record.CreateTime = DateTime.Now;
            }

            // 确保表存在
            await EnsureMonthlyTableExists(record.CreateTime);
            var tableName = GetTableName(record.CreateTime);
            
            // 使用原始SQL插入记录而不是EF Core的AddAsync，避免模型缓存问题
            await using var dbContext = CreateDbContext(record.CreateTime);
            
            // 检查是否已存在相同条码的记录
            var checkSql = $"SELECT COUNT(*) FROM {tableName} WHERE Barcode = @barcode";
            var connection = dbContext.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var checkCommand = connection.CreateCommand();
            checkCommand.CommandText = checkSql;
            checkCommand.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@barcode", record.Barcode));
            var existingCount = Convert.ToInt32(await checkCommand.ExecuteScalarAsync());
            
            if (existingCount > 0)
            {
                // 如果条码已存在，可以考虑更新
                Log.Debug("表 {TableName} 中已存在条码 {Barcode} 的记录，将添加新记录", tableName, record.Barcode);
                // 这里选择添加新记录而不是更新，因为可能表示新的扫描
            }
            
            // 构建插入SQL - 根据 PackageRecord 实际字段调整列名
            var insertSql = $@"
                INSERT INTO {tableName} (
                    Id, PackageIndex, Barcode, SegmentCode, Weight, ChuteNumber, Status, StatusDisplay,
                    CreateTime, Length, Width, Height, Volume, ErrorMessage, ImagePath
                ) VALUES (
                    NULL, @PackageIndex, @Barcode, @SegmentCode, @Weight, @ChuteNumber, @Status, @StatusDisplay,
                    @CreateTime, @Length, @Width, @Height, @Volume, @ErrorMessage, @ImagePath
                )";
                
            await using var insertCommand = connection.CreateCommand();
            insertCommand.CommandText = insertSql;
            
            // 添加参数 - 移除不存在的字段并修正字段类型
            insertCommand.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@PackageIndex", record.Index));
            insertCommand.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@Barcode", record.Barcode));
            insertCommand.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@SegmentCode", record.SegmentCode as object ?? DBNull.Value));
            insertCommand.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@Weight", record.Weight));
            _ = insertCommand.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@ChuteNumber", record.ChuteNumber.HasValue ? record.ChuteNumber.Value : DBNull.Value));
            insertCommand.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@Status", (int)record.Status));
            insertCommand.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@StatusDisplay", record.StatusDisplay));
            insertCommand.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@CreateTime", record.CreateTime.ToString("yyyy-MM-dd HH:mm:ss")));
            insertCommand.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@Length", record.Length.HasValue ? record.Length.Value : DBNull.Value));
            insertCommand.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@Width", record.Width.HasValue ? record.Width.Value : DBNull.Value));
            insertCommand.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@Height", record.Height.HasValue ? record.Height.Value : DBNull.Value));
            insertCommand.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@Volume", record.Volume.HasValue ? record.Volume.Value : DBNull.Value));
            insertCommand.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@ErrorMessage", record.ErrorMessage as object ?? DBNull.Value));
            insertCommand.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@ImagePath", record.ImagePath as object ?? DBNull.Value));
            
            await insertCommand.ExecuteNonQueryAsync();
            
            Log.Debug("添加包裹记录成功：{Barcode}，已存入表 {TableName}", record.Barcode, tableName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "添加包裹记录失败：{Barcode}", package.Barcode);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<PackageRecord?> GetPackageByBarcodeAsync(string barcode, DateTime? date = null)
    {
        try
        {
            Log.Debug("查询条码 {Barcode} 的包裹记录，指定日期：{Date}", barcode, date?.ToString("yyyy-MM-dd") ?? "null");
            
            date ??= DateTime.Today;
            var tableName = GetTableName(date.Value);
            
            // 尝试在指定表中查找
            var record = await GetPackageRecordFromTableAsync(tableName, barcode);
            if (record != null)
            {
                Log.Debug("在表 {TableName} 中找到条码 {Barcode} 的记录", tableName, barcode);
                return record;
            }
            
            // 如果未找到，且调用方未明确指定日期，则尝试在所有表中查找
            if (date.Value.Date == DateTime.Today.Date)
            {
                Log.Debug("在当前月份表中未找到条码 {Barcode}，将在所有表中查找", barcode);
                
                var tableNames = await GetAllPackageTableNamesAsync();
                // 按日期从新到旧排序
                tableNames = [.. tableNames.OrderByDescending(static t => t)];
                
                foreach (var tblName in tableNames)
                {
                    // 跳过刚才已查询过的当月表
                    if (tblName == tableName) continue;
                    
                    record = await GetPackageRecordFromTableAsync(tblName, barcode);
                    if (record != null)
                    {
                        Log.Debug("在表 {TableName} 中找到条码 {Barcode} 的记录", tblName, barcode);
                        return record;
                    }
                }
                
                Log.Debug("在所有表中都未找到条码 {Barcode} 的记录", barcode);
            }
            else
            {
                Log.Debug("在指定月份表 {TableName} 中未找到条码 {Barcode} 的记录", tableName, barcode);
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "根据条码查询包裹失败：{Barcode}", barcode);
            return null;
        }
    }

    /// <summary>
    ///     从指定表中获取包裹记录
    /// </summary>
    private async Task<PackageRecord?> GetPackageRecordFromTableAsync(string tableName, string barcode)
    {
        try
        {
            // 检查表是否存在
            if (!await TableExistsAsync(tableName))
            {
                return null;
            }
            
            await using var dbContext = CreateDbContext();
            
            // 使用原始SQL查询
            var sql = $"SELECT * FROM {tableName} WHERE Barcode = @barcode";
            try
            {
                return await dbContext.Set<PackageRecord>()
                    .FromSqlRaw(sql, new Microsoft.Data.Sqlite.SqliteParameter("@barcode", barcode))
                    .FirstOrDefaultAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "查询表 {TableName} 中的记录时出错", tableName);
                return null;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "从表 {TableName} 获取条码 {Barcode} 的记录时发生错误", tableName, barcode);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<List<PackageRecord>> GetPackagesInTimeRangeAsync(DateTime startTime, DateTime endTime)
    {
        try
        {
            // 确保所有需要的表都存在
            await EnsureDateRangeTablesExistAsync(startTime, endTime);

            // 获取所有需要查询的月份
            var startMonth = new DateTime(startTime.Year, startTime.Month, 1);
            var endMonth = new DateTime(endTime.Year, endTime.Month, 1);
            var months = new List<DateTime>();

            for (var month = startMonth; month <= endMonth; month = month.AddMonths(1)) months.Add(month);

            // 并行查询所有月份的数据
            var tasks = months.Select(async month =>
            {
                try
                {
                    await using var dbContext = CreateDbContext(month);
                    var query = dbContext.Set<PackageRecord>().AsQueryable();

                    // 如果是起始月份，添加开始时间过滤
                    if (month == startMonth)
                    {
                        query = query.Where(p => p.CreateTime >= startTime);
                    }

                    // 如果是结束月份，添加结束时间过滤
                    if (month == endMonth)
                    {
                        query = query.Where(p => p.CreateTime <= endTime);
                    }

                    return await query.ToListAsync();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "查询月份 {Month} 的数据时出错", month.ToString("yyyy-MM"));
                    return [];
                }
            });

            var results = await Task.WhenAll(tasks);
            return [.. results.SelectMany(static x => x).OrderByDescending(static p => p.CreateTime)];
        }
        catch (Exception ex)
        {
            Log.Error(ex, "查询时间范围内的包裹失败：{StartTime} - {EndTime}",
                startTime.ToString("yyyy-MM-dd HH:mm:ss"), endTime.ToString("yyyy-MM-dd HH:mm:ss"));
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<List<PackageRecord>> GetPackagesByStatusAsync(PackageStatus status, DateTime? date = null)
    {
        try
        {
            date ??= DateTime.Today;
            await using var dbContext = CreateDbContext(date);
            await EnsureMonthlyTableExists(date.Value);
            
            try
            {
                return await dbContext.Set<PackageRecord>().Where(p => p.Status == status).ToListAsync();
            }
            catch (Exception ex) when (ex.Message.Contains("no such column: p.ChuteNumber"))
            {
                Log.Warning("检测到表结构错误，正在修复...");
                await FixTableStructureAsync(date.Value);
                return await dbContext.Set<PackageRecord>().Where(p => p.Status == status).ToListAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "查询指定状态的包裹失败：{Status}", status);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<List<PackageRecord>> GetRawTableDataAsync(DateTime date)
    {
        try
        {
            await using var dbContext = CreateDbContext(date);
            await EnsureMonthlyTableExists(date);
            return await dbContext.Set<PackageRecord>().ToListAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取原始表数据失败：{Date}", date.ToString("yyyy-MM-dd"));
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<List<PackageRecord>> GetAllOfflinePackagesAsync()
    {
        var allOfflinePackages = new List<PackageRecord>();
        try
        {
            // 获取所有存在的 Packages_yyyyMM 表名
            var tableNames = await GetAllPackageTableNamesAsync();

            Log.Debug("发现 {Count} 个包裹数据表，将逐一查询离线包裹...", tableNames.Count);

            // 遍历每个表名，查询离线包裹
            foreach (var tableName in tableNames)
            {
                // 从表名解析日期（用于创建 DbContext 和可能的修复）
                if (!TryParseDateFromTableName(tableName, out var tableDate))
                {
                    Log.Warning("无法从表名 {TableName} 解析日期，跳过查询", tableName);
                    continue;
                }
                
                Log.Debug("正在查询表 {TableName} 中的离线包裹...", tableName);
                await using var dbContext = CreateDbContext(tableDate);

                try
                {
                    var offlineInTable = await dbContext.Set<PackageRecord>()
                        .Where(p => p.Status == PackageStatus.Offline)
                        .ToListAsync();

                    if (offlineInTable.Count != 0)
                    {
                        Log.Debug("在表 {TableName} 中找到 {Count} 个离线包裹", tableName, offlineInTable.Count);
                        allOfflinePackages.AddRange(offlineInTable);
                    }
                }
                catch (Exception ex) when (ex.Message.Contains("no such column:")) // 更通用的列不存在检查
                {
                    Log.Warning(ex, "查询表 {TableName} 时检测到结构问题，尝试修复...", tableName);
                    try
                    {
                        await FixTableStructureAsync(tableDate);
                        // 修复后重试查询
                        var offlineInTable = await dbContext.Set<PackageRecord>()
                            .Where(p => p.Status == PackageStatus.Offline)
                            .ToListAsync();
                         if (offlineInTable.Count != 0)
                        {
                            Log.Debug("修复后，在表 {TableName} 中找到 {Count} 个离线包裹", tableName, offlineInTable.Count);
                            allOfflinePackages.AddRange(offlineInTable);
                        }
                    }
                    catch (Exception fixEx)
                    {
                         Log.Error(fixEx, "修复表 {TableName} 结构失败，无法查询此表的离线包裹", tableName);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "查询表 {TableName} 的离线包裹时发生未知错误", tableName);
                    // 选择继续查询其他表，而不是让整个操作失败
                }
            }
            
            Log.Information("共查询到 {Count} 个离线包裹", allOfflinePackages.Count);
            // 可以选择按时间排序
            return [.. allOfflinePackages.OrderByDescending(p => p.CreateTime)];
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取所有离线包裹时发生错误");
            return []; // 返回空列表表示失败
        }
    }

    /// <inheritdoc />
    public async Task<bool> UpdatePackageStatusAsync(string barcode, PackageStatus status, string statusDisplay, DateTime? recordTime = null)
    {
        try
        {
            // 记录原始的调用参数，便于调试
            Log.Debug("尝试更新包裹状态：Barcode={Barcode}, Status={Status}, StatusDisplay={StatusDisplay}, RecordTime={RecordTime}",
                barcode, status, statusDisplay, recordTime?.ToString("yyyy-MM-dd") ?? "null");

            // 第一个尝试：使用提供的 recordTime (或当天) 查找记录
            recordTime ??= DateTime.Today; // 如果未提供时间，默认使用今天
            
            // 尝试当前月份表
            var success = await TryUpdatePackageStatusInTableAsync(barcode, status, statusDisplay, recordTime.Value);
            if (success)
            {
                return true; // 更新成功
            }
            
            // 如果未找到，尝试跨表查询：先尝试前一个月
            var previousMonth = recordTime.Value.AddMonths(-1);
            Log.Debug("在 {Month} 表中未找到包裹 {Barcode}，尝试前一个月 {PreviousMonth}", 
                recordTime.Value.ToString("yyyy-MM"), barcode, previousMonth.ToString("yyyy-MM"));
            
            success = await TryUpdatePackageStatusInTableAsync(barcode, status, statusDisplay, previousMonth);
            if (success)
            {
                return true; // 在前一个月表中找到并更新
            }
            
            // 最后，尝试所有表 (从最新的开始)
            Log.Debug("在当前月和前一个月都未找到包裹 {Barcode}，尝试在所有表中查找", barcode);
            
            var tableNames = await GetAllPackageTableNamesAsync();
            // 按日期从新到旧排序表名（假设Packages_yyyyMM格式）
            tableNames = [.. tableNames.OrderByDescending(t => t)];
            
            foreach (var tableName in tableNames)
            {
                if (!TryParseDateFromTableName(tableName, out var tableDate))
                {
                    continue; // 跳过不符合格式的表名
                }
                
                // 跳过之前已尝试过的月份
                if (tableDate.Year == recordTime.Value.Year && tableDate.Month == recordTime.Value.Month ||
                    tableDate.Year == previousMonth.Year && tableDate.Month == previousMonth.Month)
                {
                    continue;
                }
                
                success = await TryUpdatePackageStatusInTableAsync(barcode, status, statusDisplay, tableDate);
                if (success)
                {
                    Log.Information("在表 {TableName} 中找到并更新了包裹 {Barcode} 的状态", tableName, barcode);
                    return true;
                }
            }
            
            // 所有表中都未找到记录
            Log.Warning("在所有表中都未找到包裹记录：{Barcode}", barcode);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "更新包裹 {Barcode} 状态时发生错误", barcode);
            return false; // 更新失败
        }
    }
    
    /// <summary>
    ///     尝试在指定月份表中更新包裹状态
    /// </summary>
    private async Task<bool> TryUpdatePackageStatusInTableAsync(string barcode, PackageStatus status, string statusDisplay, DateTime tableDate)
    {
        try
        {
            var tableName = GetTableName(tableDate);
            
            // 检查表是否存在
            if (!await TableExistsAsync(tableName))
            {
                Log.Debug("表 {TableName} 不存在，无法更新包裹 {Barcode}", tableName, barcode);
                return false;
            }
            
            await using var dbContext = CreateDbContext(tableDate);
            
            // 使用原始SQL查询，确保查询正确的表
            var sql = $"SELECT * FROM {tableName} WHERE Barcode = @barcode";
            var record = await dbContext.Set<PackageRecord>()
                .FromSqlRaw(sql, new Microsoft.Data.Sqlite.SqliteParameter("@barcode", barcode))
                .FirstOrDefaultAsync();
            
            if (record == null)
            {
                Log.Debug("在表 {TableName} 中未找到包裹 {Barcode}", tableName, barcode);
                return false;
            }
            
            // 记录更新前的状态，便于调试
            Log.Debug("在表 {TableName} 中找到包裹 {Barcode}，原状态={OldStatus}，新状态={NewStatus}", 
                tableName, barcode, record.Status, status);
            
            // 更新状态
            record.Status = status;
            record.StatusDisplay = statusDisplay;
            
            // 使用原始SQL执行更新，确保更新正确的表
            var updateSql = $@"
                UPDATE {tableName}
                SET Status = @status, StatusDisplay = @statusDisplay
                WHERE Barcode = @barcode";
                
            await dbContext.Database.ExecuteSqlRawAsync(updateSql,
                new Microsoft.Data.Sqlite.SqliteParameter("@status", (int)status),
                new Microsoft.Data.Sqlite.SqliteParameter("@statusDisplay", statusDisplay),
                new Microsoft.Data.Sqlite.SqliteParameter("@barcode", barcode));
            
            Log.Information("成功更新表 {TableName} 中包裹 {Barcode} 的状态为 {Status}", tableName, barcode, status);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "尝试在表 {TableName} 中更新包裹 {Barcode} 状态时出错", GetTableName(tableDate), barcode);
            return false;
        }
    }
    
    /// <summary>
    ///     检查表是否存在
    /// </summary>
    private async Task<bool> TableExistsAsync(string tableName)
    {
        // 先检查缓存
        if (_tableExistsCache.TryGetValue(tableName, out var exists) && exists)
        {
            return true;
        }
        
        try
        {
            await using var dbContext = CreateDbContext();
            var connection = dbContext.Database.GetDbConnection();
            await connection.OpenAsync();
            
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}'";
            var result = await command.ExecuteScalarAsync();
            
            exists = Convert.ToInt32(result) > 0;
            
            if (exists)
            {
                _tableExistsCache.TryAdd(tableName, true);
            }
            
            return exists;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "检查表 {TableName} 是否存在时出错", tableName);
            return false;
        }
    }

    /// <summary>
    ///     初始化数据库
    /// </summary>
    private async Task InitializeDatabase()
    {
        try
        {
            Log.Information("正在初始化包裹数据库...");

            // 确保数据库目录存在
            var dbDirectory = Path.GetDirectoryName(_dbPath);
            if (!Directory.Exists(dbDirectory))
            {
                Directory.CreateDirectory(dbDirectory!);
                Log.Information("创建数据库目录：{Directory}", dbDirectory);
            }

            // 创建当月和下月的表
            var currentMonth = DateTime.Today;
            var nextMonth = currentMonth.AddMonths(1);

            await EnsureMonthlyTableExists(currentMonth);
            await EnsureMonthlyTableExists(nextMonth);

            // 清理过期的表（保留近6个月的数据）
            await CleanupOldTables(6);

            Log.Information("包裹数据库初始化完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化包裹数据库时发生错误");
            throw;
        }
    }

    /// <summary>
    ///     获取表名
    /// </summary>
    private static string GetTableName(DateTime date)
    {
        return $"Packages_{date:yyyyMM}";
    }

    /// <summary>
    ///     确保月度表存在
    /// </summary>
    private async Task EnsureMonthlyTableExists(DateTime date)
    {
        var tableName = GetTableName(date);

        // 检查缓存
        if (_tableExistsCache.TryGetValue(tableName, out var exists) && exists) return;

        Log.Debug("检查数据表是否存在：{TableName}", tableName);

        await using var dbContext = CreateDbContext(date);
        try
        {
            // 确保数据库存在
            if (!File.Exists(_dbPath))
            {
                Log.Information("创建新的数据库文件：{DbPath}", _dbPath);
                await dbContext.Database.EnsureCreatedAsync();
            }

            // 检查表是否存在
            var checkTableSql = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}'";
            var connection = dbContext.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = checkTableSql;
            var result = await command.ExecuteScalarAsync();
            var tableExists = Convert.ToInt32(result) > 0;

            if (!tableExists)
                await CreateMonthlyTableAsync(dbContext, tableName);
            else
                Log.Debug("数据表已存在：{TableName}", tableName);

            // 更新缓存
            _tableExistsCache.TryAdd(tableName, true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "确保数据表存在时发生错误：{TableName}", tableName);
            throw;
        }
    }

    /// <summary>
    ///     创建月度数据表
    /// </summary>
    private static async Task CreateMonthlyTableAsync(DbContext dbContext, string tableName)
    {
        try
        {
            // 删除可能存在的旧表
            try
            {
                var dropTableSql = $"DROP TABLE IF EXISTS {tableName}";
                await dbContext.Database.ExecuteSqlRawAsync(dropTableSql);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "删除旧表时发生错误：{TableName}", tableName);
            }

            // 创建新表
            var createTableSql = $"""
                                                  CREATE TABLE {tableName} (
                                                      Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                                      PackageIndex INTEGER NOT NULL,
                                                      Barcode TEXT(50) NOT NULL,
                                                      SegmentCode TEXT(50),
                                                      Weight REAL,
                                                      ChuteNumber INTEGER,
                                                      Status INTEGER,
                                                      StatusDisplay TEXT(50),
                                                      CreateTime TEXT,
                                                      Length REAL,
                                                      Width REAL,
                                                      Height REAL,
                                                      Volume REAL,
                                                      Information TEXT(500),
                                                      ErrorMessage TEXT(500),
                                                      ImagePath TEXT(255)
                                                  )
                                  """;

            await dbContext.Database.ExecuteSqlRawAsync(createTableSql);

            // 创建联合索引
            var createIndexSql = $"""

                                                  CREATE INDEX IX_{tableName}_CreateTime_Barcode 
                                                  ON {tableName}(CreateTime, Barcode)
                                  """;

            await dbContext.Database.ExecuteSqlRawAsync(createIndexSql);

            Log.Information("数据表创建成功：{TableName}", tableName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "创建数据表时发生错误：{TableName}", tableName);
            throw;
        }
    }

    /// <summary>
    ///     批量确保多个月份的数据表存在
    /// </summary>
    private async Task EnsureDateRangeTablesExistAsync(DateTime startDate, DateTime endDate)
    {
        try
        {
            // 确保日期范围有效
            if (startDate > endDate)
            {
                Log.Warning("批量检查表时日期范围无效：开始日期 {StartDate} 大于结束日期 {EndDate}",
                    startDate.ToString("yyyy-MM-dd"), endDate.ToString("yyyy-MM-dd"));
                (startDate, endDate) = (endDate, startDate);
            }

            Log.Information("批量检查日期范围表: {StartDate} 到 {EndDate}",
                startDate.ToString("yyyy-MM-dd"), endDate.ToString("yyyy-MM-dd"));

            // 获取所有需要检查的月份
            var startMonth = new DateTime(startDate.Year, startDate.Month, 1);
            var endMonth = new DateTime(endDate.Year, endDate.Month, 1);

            for (var month = startMonth; month <= endMonth; month = month.AddMonths(1))
                await EnsureMonthlyTableExists(month);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "批量确保数据表存在时发生错误");
            throw;
        }
    }

    /// <summary>
    ///     清理旧表
    /// </summary>
    private async Task CleanupOldTables(int keepMonths)
    {
        try
        {
            var cutoffDate = DateTime.Today.AddMonths(-keepMonths);
            await using var dbContext = CreateDbContext();

            // 获取所有表名
            const string sql = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'Packages_%'";
            var connection = dbContext.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var tables = new List<string>();

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));

            foreach (var table in tables)
            {
                // 解析表名中的日期
                if (table.Length <= 9 || !DateTime.TryParseExact(table[9..], "yyyyMM", null,
                        DateTimeStyles.None, out var tableDate)) continue;
                if (tableDate >= cutoffDate) continue;

                Log.Information("清理旧表：{Table}", table);
                await dbContext.Database.ExecuteSqlAsync($"DROP TABLE IF EXISTS {table}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "清理旧表时发生错误");
        }
    }

    /// <summary>
    ///     修复表结构
    /// </summary>
    private async Task FixTableStructureAsync(DateTime date)
    {
        try
        {
            var tableName = GetTableName(date);
            await using var dbContext = CreateDbContext(date);

            // 检查表是否存在
            var checkTableSql = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}'";
            var connection = dbContext.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = checkTableSql;
            var result = await command.ExecuteScalarAsync();
            var tableExists = Convert.ToInt32(result) > 0;

            if (!tableExists)
            {
                // 如果表不存在，直接创建新表
                await CreateMonthlyTableAsync(dbContext, tableName);
                Log.Information("创建新表：{TableName}", tableName);
                return;
            }

            // 检查表结构
            var checkColumnSql = $"PRAGMA table_info({tableName})";
            command.CommandText = checkColumnSql;
            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1)); // 列名在第二列
            }

            // 定义所有必需的列
            var requiredColumns = new Dictionary<string, string>
            {
                { "Id", "INTEGER PRIMARY KEY AUTOINCREMENT" },
                { "PackageIndex", "INTEGER NOT NULL" },
                { "Barcode", "TEXT(50) NOT NULL" },
                { "SegmentCode", "TEXT(50)" },
                { "Weight", "REAL" },
                { "ChuteNumber", "INTEGER" },
                { "Status", "INTEGER" },
                { "StatusDisplay", "TEXT(50)" },
                { "CreateTime", "TEXT" },
                { "Length", "REAL" },
                { "Width", "REAL" },
                { "Height", "REAL" },
                { "Volume", "REAL" },
                { "Information", "TEXT(500)" },
                { "ErrorMessage", "TEXT(500)" },
                { "ImagePath", "TEXT(255)" }
            };

            // 检查并添加缺失的列
            foreach (var column in requiredColumns)
            {
                if (!columns.Contains(column.Key))
                {
                    var addColumnSql = $"ALTER TABLE {tableName} ADD COLUMN {column.Key} {column.Value}";
                    await dbContext.Database.ExecuteSqlRawAsync(addColumnSql);
                    Log.Information("已添加列 {Column} 到表：{TableName}", column.Key, tableName);
                }
            }

            // 检查是否需要删除ChuteName列
            if (columns.Contains("ChuteName"))
            {
                // 由于SQLite不支持直接删除列，我们需要创建一个新表并迁移数据
                var tempTableName = $"{tableName}_temp";
                
                // 创建临时表
                var createTempTableSql = $"""
                    CREATE TABLE {tempTableName} (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        PackageIndex INTEGER NOT NULL,
                        Barcode TEXT(50) NOT NULL,
                        SegmentCode TEXT(50),
                        Weight REAL,
                        ChuteNumber INTEGER,
                        Status INTEGER,
                        StatusDisplay TEXT(50),
                        CreateTime TEXT,
                        Length REAL,
                        Width REAL,
                        Height REAL,
                        Volume REAL,
                        Information TEXT(500),
                        ErrorMessage TEXT(500),
                        ImagePath TEXT(255)
                    )
                """;
                await dbContext.Database.ExecuteSqlRawAsync(createTempTableSql);

                // 迁移数据
                var migrateDataSql = $"""
                    INSERT INTO {tempTableName} (
                        Id, PackageIndex, Barcode, SegmentCode, Weight, Status, StatusDisplay,
                        CreateTime, Length, Width, Height, Volume, Information, ErrorMessage, ImagePath
                    )
                    SELECT 
                        Id, PackageIndex, Barcode, SegmentCode, Weight, Status, StatusDisplay,
                        CreateTime, Length, Width, Height, Volume, Information, ErrorMessage, ImagePath
                    FROM {tableName}
                """;
                await dbContext.Database.ExecuteSqlRawAsync(migrateDataSql);

                // 删除旧表
                var dropOldTableSql = $"DROP TABLE {tableName}";
                await dbContext.Database.ExecuteSqlRawAsync(dropOldTableSql);

                // 重命名临时表
                var renameTableSql = $"ALTER TABLE {tempTableName} RENAME TO {tableName}";
                await dbContext.Database.ExecuteSqlRawAsync(renameTableSql);

                Log.Information("已从表 {TableName} 中移除ChuteName列", tableName);
            }

            // 创建索引（如果不存在）
            var checkIndexSql = $"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_{tableName}_CreateTime_Barcode'";
            command.CommandText = checkIndexSql;
            result = await command.ExecuteScalarAsync();
            var indexExists = Convert.ToInt32(result) > 0;

            if (!indexExists)
            {
                var createIndexSql = $"""
                    CREATE INDEX IX_{tableName}_CreateTime_Barcode 
                    ON {tableName}(CreateTime, Barcode)
                """;
                await dbContext.Database.ExecuteSqlRawAsync(createIndexSql);
                Log.Information("已创建索引：IX_{tableName}_CreateTime_Barcode", tableName);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "修复表结构时发生错误：{Date}", date.ToString("yyyy-MM"));
            throw;
        }
    }

    /// <summary>
    ///     创建数据库上下文
    /// </summary>
    private PackageDbContext CreateDbContext(DateTime? date = null)
    {
        return new PackageDbContext(_options, date);
    }

    /// <summary>
    ///     获取所有存在的 Packages_yyyyMM 表名
    /// </summary>
    private async Task<List<string>> GetAllPackageTableNamesAsync()
    {
        var tableNames = new List<string>();
        try
        {
             await using var dbContext = CreateDbContext(); // 使用默认上下文获取连接
             var connection = dbContext.Database.GetDbConnection();
             await connection.OpenAsync();
             await using var command = connection.CreateCommand();
             command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'Packages_%'";
             
             await using var reader = await command.ExecuteReaderAsync();
             while (await reader.ReadAsync())
             {
                 tableNames.Add(reader.GetString(0));
             }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取所有包裹表名时出错");
        }
        return tableNames;
    }

    /// <summary>
    ///     尝试从表名解析日期
    /// </summary>
    private static bool TryParseDateFromTableName(string tableName, out DateTime date)
    {
        date = default;
        if (tableName.Length <= 9 || !tableName.StartsWith("Packages_")) return false;
        return DateTime.TryParseExact(tableName[9..], "yyyyMM", CultureInfo.InvariantCulture,
                                    DateTimeStyles.None, out date);
    }
}
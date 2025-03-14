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
            await using var dbContext = CreateDbContext(record.CreateTime);
            await EnsureMonthlyTableExists(record.CreateTime);
            await dbContext.Set<PackageRecord>().AddAsync(record);
            await dbContext.SaveChangesAsync();
            Log.Debug("添加包裹记录成功：{Barcode}", record.Barcode);
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
            date ??= DateTime.Today;
            await using var dbContext = CreateDbContext(date);
            await EnsureMonthlyTableExists(date.Value);
            return await dbContext.Set<PackageRecord>().FirstOrDefaultAsync(p => p.Barcode == barcode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "根据条码查询包裹失败：{Barcode}", barcode);
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
            return results.SelectMany(static x => x).OrderByDescending(static p => p.CreateTime).ToList();
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
            return await dbContext.Set<PackageRecord>().Where(p => p.Status == status).ToListAsync();
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
            return new List<PackageRecord>();
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
                                                      ChuteName TEXT,
                                                      Status INTEGER,
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
    ///     创建数据库上下文
    /// </summary>
    private PackageDbContext CreateDbContext(DateTime? date = null)
    {
        return new PackageDbContext(_options, date);
    }
}
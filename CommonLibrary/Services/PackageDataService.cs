using System.IO;
using CommonLibrary.Data;
using CommonLibrary.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CommonLibrary.Services;

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
public class PackageDataService : IPackageDataService
{
    private readonly string _dbPath;

    /// <summary>
    ///     构造函数
    /// </summary>
    public PackageDataService()
    {
        _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "packages.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

        // 初始化数据库
        InitializeDatabase().Wait();
    }

    /// <inheritdoc />
    public async Task AddPackageAsync(PackageInfo package)
    {
        // 确保当日表存在
        await EnsureDailyTableExists(package.CreateTime);

        await using var dbContext = CreateDbContext(package.CreateTime);
        var record = PackageRecord.FromPackageInfo(package);
        await dbContext.Packages.AddAsync(record);
        await dbContext.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task<PackageRecord?> GetPackageByBarcodeAsync(string barcode, DateTime? date = null)
    {
        date ??= DateTime.Today;
        await EnsureDailyTableExists(date.Value);
        await using var dbContext = CreateDbContext(date);
        return await dbContext.Packages
            .FirstOrDefaultAsync(p => p.Barcode == barcode);
    }

    /// <inheritdoc />
    public async Task<List<PackageRecord>> GetPackagesInTimeRangeAsync(DateTime startTime, DateTime endTime)
    {
        var result = new List<PackageRecord>();

        // 一次性检查并创建所有需要的表
        await EnsureDateRangeTablesExistAsync(startTime.Date, endTime.Date);

        // 逐天查询数据
        var currentDate = startTime.Date;
        while (currentDate <= endTime.Date)
        {
            try
            {
                await using var dbContext = CreateDbContext(currentDate);

                // 常规 EF 查询可能遇到日期比较问题，添加日志查看实际执行的 SQL
                var query = dbContext.Packages
                    .Where(p => p.CreateTime >= startTime && p.CreateTime <= endTime)
                    .OrderByDescending(p => p.CreateTime);

                Log.Debug("SQL查询: {Sql}", query.ToQueryString());

                // 尝试改用原生SQL查询，避免日期比较问题
                var tableName = $"Packages_{currentDate:yyyyMMdd}";

                // 确保可以连接到数据库
                if (await dbContext.Database.CanConnectAsync())
                {
                    // 使用 EF Core 查询
                    var efRecords = await query.ToListAsync();

                    // 如果没有结果，尝试使用原生SQL
                    if (efRecords.Count == 0)
                    {
                        Log.Information("尝试使用原生SQL查询表 {TableName}", tableName);

                        // 格式化日期为 ISO 8601 格式 (SQLite 推荐格式)
                        var startStr = startTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
                        var endStr = endTime.ToString("yyyy-MM-dd HH:mm:ss.fff");

                        var sql =
                            $"SELECT * FROM {tableName} WHERE CreateTime BETWEEN @startTime AND @endTime ORDER BY CreateTime DESC";

                        await using var connection = new SqliteConnection(dbContext.Database.GetConnectionString());
                        await connection.OpenAsync();

                        await using var command = connection.CreateCommand();
                        command.CommandText = sql;
                        command.Parameters.AddWithValue("@startTime", startStr);
                        command.Parameters.AddWithValue("@endTime", endStr);

                        List<PackageRecord> sqlRecords = [];

                        await using var reader = await command.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                            try
                            {
                                var record = new PackageRecord
                                {
                                    Id = (int)reader.GetInt64(reader.GetOrdinal("Id")),
                                    Barcode = reader.GetString(reader.GetOrdinal("Barcode"))
                                };

                                // 安全地获取可空字段
                                if (!reader.IsDBNull(reader.GetOrdinal("CreateTime")))
                                {
                                    var createTimeStr = reader.GetString(reader.GetOrdinal("CreateTime"));
                                    if (DateTime.TryParse(createTimeStr, out var createTime))
                                        record.CreateTime = createTime;
                                }

                                if (!reader.IsDBNull(reader.GetOrdinal("Status")))
                                    record.Status = (PackageStatus)reader.GetInt32(reader.GetOrdinal("Status"));

                                // 添加其他需要的字段
                                sqlRecords.Add(record);
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "解析SQL查询结果时出错");
                            }

                        Log.Information("SQL查询返回了 {Count} 条记录", sqlRecords.Count);
                        result.AddRange(sqlRecords);
                    }
                    else
                    {
                        Log.Information("EF Core查询返回了 {Count} 条记录", efRecords.Count);
                        result.AddRange(efRecords);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "查询日期 {Date} 的数据时出错", currentDate.ToString("yyyy-MM-dd"));
            }

            currentDate = currentDate.AddDays(1);
        }

        return [.. result.OrderByDescending(p => p.CreateTime)];
    }

    /// <inheritdoc />
    public async Task<List<PackageRecord>> GetPackagesByStatusAsync(PackageStatus status, DateTime? date = null)
    {
        date ??= DateTime.Today;

        // 仅检查指定日期的表
        await EnsureDailyTableExists(date.Value);
        await using var dbContext = CreateDbContext(date);
        return await dbContext.Packages
            .Where(p => p.Status == status)
            .OrderByDescending(p => p.CreateTime)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<PackageRecord>> GetRawTableDataAsync(DateTime date)
    {
        try
        {
            Log.Information("直接查询表 Packages_{Date} 中的所有数据", date.ToString("yyyyMMdd"));

            // 确保表存在
            await EnsureDailyTableExists(date);

            // 直接查询所有数据，不做过滤
            await using var dbContext = CreateDbContext(date);

            // 记录 SQL 查询语句
            var sql = dbContext.Packages.ToQueryString();
            Log.Debug("执行 SQL 查询: {Sql}", sql);

            var result = await dbContext.Packages.ToListAsync();
            Log.Information("表 Packages_{Date} 查询到 {Count} 条数据", date.ToString("yyyyMMdd"), result.Count);

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "直接查询表数据时出错: {Date}", date.ToString("yyyyMMdd"));
            return [];
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

            // 创建今天的数据表
            await EnsureDailyTableExists(DateTime.Today);

            // 创建明天的数据表（跨天准备）
            await EnsureDailyTableExists(DateTime.Today.AddDays(1));

            Log.Information("包裹数据库初始化完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化包裹数据库时发生错误");
            throw;
        }
    }

    /// <summary>
    ///     批量确保多个日期的数据表存在
    /// </summary>
    /// <param name="startDate">开始日期</param>
    /// <param name="endDate">结束日期</param>
    private async Task EnsureDateRangeTablesExistAsync(DateTime startDate, DateTime endDate)
    {
        try
        {
            // 确保日期范围有效
            if (startDate > endDate)
            {
                Log.Warning("批量检查表时日期范围无效：开始日期 {StartDate} 大于结束日期 {EndDate}",
                    startDate.ToString("yyyy-MM-dd"), endDate.ToString("yyyy-MM-dd"));
                // 交换日期，确保逻辑可以继续
                (startDate, endDate) = (endDate, startDate);
            }

            Log.Information("批量检查日期范围表: {StartDate} 到 {EndDate}",
                startDate.ToString("yyyy-MM-dd"), endDate.ToString("yyyy-MM-dd"));

            // 收集所有需要检查的表名
            var tableNames = new List<string>();
            var currentDate = startDate.Date;

            while (currentDate <= endDate.Date)
            {
                tableNames.Add($"Packages_{currentDate:yyyyMMdd}");
                currentDate = currentDate.AddDays(1);
            }

            // 检查表名列表是否为空
            if (tableNames.Count == 0)
            {
                Log.Warning("批量检查表时没有找到需要检查的表名");
                return;
            }

            // 一次性查询所有已存在的表
            await using var dbContext = CreateDbContext();

            // 确保数据库存在
            if (!File.Exists(_dbPath))
            {
                Log.Information("创建新的数据库文件：{DbPath}", _dbPath);
                await dbContext.Database.EnsureCreatedAsync();
            }

            // 构建查询所有表的SQL
            var tablesString = string.Join("','", tableNames);
            var checkTablesSql = $"SELECT name FROM sqlite_master WHERE type='table' AND name IN ('{tablesString}')";

            List<string> existingTables = [];
            try
            {
                // 执行查询获取已存在的表
                await using var connection = new SqliteConnection(dbContext.Database.GetConnectionString());
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = checkTablesSql;

                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) existingTables.Add(reader.GetString(0));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "批量检查表存在时发生错误，将回退到逐个检查表");
                // 如果批量查询失败，回退到逐个检查表
                foreach (var tableName in tableNames)
                    await EnsureDailyTableExists(DateTime.ParseExact(
                        tableName.Substring("Packages_".Length), "yyyyMMdd", null));
                return;
            }

            // 创建不存在的表
            currentDate = startDate.Date;
            while (currentDate <= endDate.Date)
            {
                var tableName = $"Packages_{currentDate:yyyyMMdd}";
                if (!existingTables.Contains(tableName))
                {
                    Log.Information("创建新的数据表：{TableName}", tableName);
                    await CreateDailyTableAsync(dbContext, tableName);
                }

                currentDate = currentDate.AddDays(1);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "批量确保数据表存在时发生错误");
            throw;
        }
    }

    /// <summary>
    ///     创建指定表名的数据表
    /// </summary>
    private static async Task CreateDailyTableAsync(PackageDbContext dbContext, string tableName)
    {
        try
        {
            // 删除可能存在的旧表定义
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
            var createTableSql = $@"
                CREATE TABLE {tableName} (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Barcode TEXT NOT NULL,
                    SegmentCode TEXT,
                    Weight REAL,
                    ChuteName TEXT,
                    Status INTEGER,
                    CreateTime TEXT,
                    Length REAL,
                    Width REAL,
                    Height REAL,
                    Volume REAL,
                    Information TEXT,
                    ErrorMessage TEXT,
                    ImagePath TEXT
                )";

            await dbContext.Database.ExecuteSqlRawAsync(createTableSql);

            // 创建索引
            var createIndexSql = $@"
                CREATE INDEX IX_{tableName}_CreateTime 
                ON {tableName}(CreateTime)";

            await dbContext.Database.ExecuteSqlRawAsync(createIndexSql);

            Log.Information("数据表创建成功：{TableName}", tableName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "创建数据表时发生错误：{TableName}", tableName);
            throw;
        }
    }

    private PackageDbContext CreateDbContext(DateTime? date = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PackageDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        return new PackageDbContext(optionsBuilder.Options, date);
    }

    /// <summary>
    ///     确保当日数据表存在
    /// </summary>
    private async Task EnsureDailyTableExists(DateTime date)
    {
        var tableName = $"Packages_{date:yyyyMMdd}";
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
            using var command = connection.CreateCommand();
            command.CommandText = checkTableSql;
            var result = await command.ExecuteScalarAsync();
            var tableExists = Convert.ToInt32(result) > 0;

            if (!tableExists)
                await CreateDailyTableAsync(dbContext, tableName);
            else
                Log.Debug("数据表已存在：{TableName}", tableName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "确保数据表存在时发生错误：{TableName}", tableName);
            throw;
        }
    }
}
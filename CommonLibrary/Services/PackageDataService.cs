using System.IO;
using CommonLibrary.Data;
using CommonLibrary.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Microsoft.Data.Sqlite;

namespace CommonLibrary.Services;

/// <summary>
/// 包裹数据服务
/// </summary>
public interface IPackageDataService
{
    /// <summary>
    /// 添加包裹记录
    /// </summary>
    Task AddPackageAsync(PackageInfo package);
    
    /// <summary>
    /// 根据条码查询包裹
    /// </summary>
    Task<PackageRecord?> GetPackageByBarcodeAsync(string barcode, DateTime? date = null);
    
    /// <summary>
    /// 查询指定时间范围内的包裹
    /// </summary>
    Task<List<PackageRecord>> GetPackagesInTimeRangeAsync(DateTime startTime, DateTime endTime);
    
    /// <summary>
    /// 查询指定状态的包裹
    /// </summary>
    Task<List<PackageRecord>> GetPackagesByStatusAsync(PackageStatus status, DateTime? date = null);
}

/// <summary>
/// 包裹数据服务实现
/// </summary>
public class PackageDataService : IPackageDataService
{
    private readonly string _dbPath;

    /// <summary>
    /// 构造函数
    /// </summary>
    public PackageDataService()
    {
        _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "packages.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        
        // 初始化数据库
        InitializeDatabase().Wait();
    }

    /// <summary>
    /// 初始化数据库
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

    private PackageDbContext CreateDbContext(DateTime? date = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PackageDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        return new PackageDbContext(optionsBuilder.Options, date);
    }

    /// <summary>
    /// 确保当日数据表存在
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
            var tableExists = await dbContext.Database
                .ExecuteSqlRawAsync(checkTableSql) > 0;

            if (!tableExists)
            {
                Log.Information("创建新的数据表：{TableName}", tableName);
                
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
            else
            {
                Log.Debug("数据表已存在：{TableName}", tableName);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "确保数据表存在时发生错误：{TableName}", tableName);
            throw;
        }
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
        var currentDate = startTime.Date;
        
        while (currentDate <= endTime.Date)
        {
            await EnsureDailyTableExists(currentDate);
            await using var dbContext = CreateDbContext(currentDate);
            if (await dbContext.Database.CanConnectAsync())
            {
                var dayRecords = await dbContext.Packages
                    .Where(p => p.CreateTime >= startTime && p.CreateTime <= endTime)
                    .OrderByDescending(p => p.CreateTime)
                    .ToListAsync();
                result.AddRange(dayRecords);
            }
            currentDate = currentDate.AddDays(1);
        }

        return result.OrderByDescending(p => p.CreateTime).ToList();
    }

    /// <inheritdoc />
    public async Task<List<PackageRecord>> GetPackagesByStatusAsync(PackageStatus status, DateTime? date = null)
    {
        date ??= DateTime.Today;
        await EnsureDailyTableExists(date.Value);
        await using var dbContext = CreateDbContext(date);
        return await dbContext.Packages
            .Where(p => p.Status == status)
            .OrderByDescending(p => p.CreateTime)
            .ToListAsync();
    }
} 
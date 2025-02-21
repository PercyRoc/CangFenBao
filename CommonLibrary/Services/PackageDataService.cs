using System.IO;
using CommonLibrary.Data;
using CommonLibrary.Models;
using Microsoft.EntityFrameworkCore;

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
    private DateTime _lastInitializedDate;

    /// <summary>
    /// 构造函数
    /// </summary>
    public PackageDataService()
    {
        _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "packages.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _lastInitializedDate = DateTime.MinValue;
        InitializeDatabase(DateTime.Today).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 初始化数据库
    /// </summary>
    private async Task InitializeDatabase(DateTime date)
    {
        if (date.Date == _lastInitializedDate.Date) return;
        
        await using var dbContext = CreateDbContext(date);
        await dbContext.Database.EnsureCreatedAsync();
        _lastInitializedDate = date.Date;
    }

    private PackageDbContext CreateDbContext(DateTime? date = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PackageDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        var context = new PackageDbContext(optionsBuilder.Options, date);
        
        // 确保当日表存在
        if (date.HasValue && date.Value.Date != _lastInitializedDate.Date)
        {
            InitializeDatabase(date.Value).GetAwaiter().GetResult();
        }
        
        return context;
    }

    /// <inheritdoc />
    public async Task AddPackageAsync(PackageInfo package)
    {
        await using var dbContext = CreateDbContext(package.CreateTime);
        var record = PackageRecord.FromPackageInfo(package);
        await dbContext.Packages.AddAsync(record);
        await dbContext.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task<PackageRecord?> GetPackageByBarcodeAsync(string barcode, DateTime? date = null)
    {
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
        await using var dbContext = CreateDbContext(date);
        return await dbContext.Packages
            .Where(p => p.Status == status)
            .OrderByDescending(p => p.CreateTime)
            .ToListAsync();
    }
} 
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

    /// <summary>
    /// 构造函数
    /// </summary>
    public PackageDataService()
    {
        _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "packages.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
    }

    private PackageDbContext CreateDbContext(DateTime? date = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PackageDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        return new PackageDbContext(optionsBuilder.Options, date);
    }

    /// <inheritdoc />
    public async Task AddPackageAsync(PackageInfo package)
    {
        await using var dbContext = CreateDbContext(package.CreateTime);
        await dbContext.Database.EnsureCreatedAsync();
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
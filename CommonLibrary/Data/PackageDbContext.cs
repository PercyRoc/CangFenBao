using CommonLibrary.Models;
using Microsoft.EntityFrameworkCore;

namespace CommonLibrary.Data;

/// <summary>
/// 包裹数据库上下文
/// </summary>
public class PackageDbContext : DbContext
{
    private readonly DateTime _date;
    
    /// <summary>
    /// 包裹数据
    /// </summary>
    public DbSet<PackageRecord> Packages { get; set; } = null!;

    /// <summary>
    /// 构造函数
    /// </summary>
    public PackageDbContext(DbContextOptions<PackageDbContext> options, DateTime? date = null) : base(options)
    {
        _date = date ?? DateTime.Today;
    }

    /// <summary>
    /// 配置模型
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var tableName = $"Packages_{_date:yyyyMMdd}";
        
        modelBuilder.Entity<PackageRecord>(entity =>
        {
            entity.ToTable(tableName);
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Barcode).IsRequired();
            entity.Property(e => e.SegmentCode);
            entity.Property(e => e.Weight);
            entity.Property(e => e.ChuteName);
            entity.Property(e => e.Status);
            entity.Property(e => e.CreateTime);
            entity.Property(e => e.Length);
            entity.Property(e => e.Width);
            entity.Property(e => e.Height);
            entity.Property(e => e.Volume);
            entity.Property(e => e.Information);
            entity.Property(e => e.ErrorMessage);
            entity.Property(e => e.ImagePath);
        });

        modelBuilder.Entity<PackageRecord>()
            .HasIndex(p => p.CreateTime)
            .HasDatabaseName($"IX_{tableName}_CreateTime");

        base.OnModelCreating(modelBuilder);
    }
} 
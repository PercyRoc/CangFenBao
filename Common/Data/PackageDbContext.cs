using Microsoft.EntityFrameworkCore;

namespace Common.Data;

/// <summary>
///     包裹数据库上下文
/// </summary>
/// <remarks>
///     构造函数
/// </remarks>
public class PackageDbContext(DbContextOptions<PackageDbContext> options, DateTime? date = null) : DbContext(options)
{
    private readonly DateTime _date = date ?? DateTime.Today;

    /// <summary>
    ///     配置模型
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var tableName = $"Packages_{_date:yyyyMM}";

        modelBuilder.Entity<PackageRecord>(entity =>
        {
            entity.ToTable(tableName);
            entity.HasKey(static e => e.Id);
            entity.Property(static e => e.Id).ValueGeneratedOnAdd();
            entity.Property(static e => e.Index).HasColumnName("PackageIndex");
            entity.Property(static e => e.Barcode).IsRequired();
            entity.Property(static e => e.SegmentCode);
            entity.Property(static e => e.Weight);
            entity.Property(static e => e.ChuteName);
            entity.Property(static e => e.Status);
            entity.Property(static e => e.CreateTime);
            entity.Property(static e => e.Length);
            entity.Property(static e => e.Width);
            entity.Property(static e => e.Height);
            entity.Property(static e => e.Volume);
            entity.Property(static e => e.Information);
            entity.Property(static e => e.ErrorMessage);
            entity.Property(static e => e.ImagePath);
        });

        modelBuilder.Entity<PackageRecord>()
            .HasIndex(static p => p.CreateTime)
            .HasDatabaseName($"IX_{tableName}_CreateTime");

        base.OnModelCreating(modelBuilder);
    }
}
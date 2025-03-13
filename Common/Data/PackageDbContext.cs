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
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Index).HasColumnName("PackageIndex");
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
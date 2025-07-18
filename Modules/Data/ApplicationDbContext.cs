using System.IO;
using Microsoft.EntityFrameworkCore;
using ShanghaiModuleBelt.Models;

namespace ShanghaiModuleBelt.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
        // 确保数据库文件存在
        Database.EnsureCreated();
    }

    public DbSet<RetryRecord> RetryRecords { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RetryRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Barcode).IsRequired();
            entity.Property(e => e.Company).IsRequired();
            entity.Property(e => e.RequestData).IsRequired();
            entity.Property(e => e.CreateTime).IsRequired();
        });
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (optionsBuilder.IsConfigured) return;
        // 使用相对路径存储数据库文件
        var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "retry.db");
        // 确保目录存在
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        optionsBuilder.UseSqlite($"Data Source={dbPath}");
    }
}
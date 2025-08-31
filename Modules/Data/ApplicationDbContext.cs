using System.Data.Common;
using System.Diagnostics;
using System.IO;
using Microsoft.EntityFrameworkCore;
using ShanghaiModuleBelt.Models;

namespace ShanghaiModuleBelt.Data;

public sealed class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
        // 确保数据库文件存在
        Database.EnsureCreated();

        // 检查并更新现有数据库架构
        UpdateDatabaseSchema();
    }

    // 重传记录表已移除

    /// <summary>
    ///     更新现有数据库架构以应用字段长度限制
    /// </summary>
    private void UpdateDatabaseSchema()
    {
        try
        {
            // 检查是否需要更新表结构
            var connection = Database.GetDbConnection();
            connection.Open();

            using var command = connection.CreateCommand();

            // 已移除重传记录相关表的自动迁移逻辑

            connection.Close();
        }
        catch (Exception ex)
        {
            // 记录错误但不阻止应用程序启动
            Debug.WriteLine($"更新数据库架构时发生错误: {ex.Message}");
        }
    }

    // 已移除重传记录相关表的自动迁移逻辑

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 已移除重传记录模型映射
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
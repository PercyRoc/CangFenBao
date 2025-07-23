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
    
    /// <summary>
    /// 更新现有数据库架构以应用字段长度限制
    /// </summary>
    private void UpdateDatabaseSchema()
    {
        try
        {
            // 检查是否需要更新表结构
            var connection = Database.GetDbConnection();
            connection.Open();
            
            using var command = connection.CreateCommand();
            
            // 检查表是否存在列长度限制
            command.CommandText = "PRAGMA table_info(RetryRecords)";
            using var reader = command.ExecuteReader();
            
            bool needsUpdate = false;
             while (reader.Read())
             {
                 var columnName = reader.GetString(1); // name 列的索引是 1
                 var columnType = reader.GetString(2); // type 列的索引是 2
                 
                 // 检查字符串字段是否有长度限制
                 if ((columnName == "Barcode" || columnName == "Company" || 
                      columnName == "RequestData" || columnName == "ErrorMessage") &&
                     columnType == "TEXT")
                 {
                     needsUpdate = true;
                     break;
                 }
             }
            reader.Close();
            
            if (needsUpdate)
            {
                // 由于 SQLite 不支持直接修改列类型，我们需要重建表
                RebuildRetryRecordsTable(connection);
            }
            
            connection.Close();
        }
        catch (Exception ex)
        {
            // 记录错误但不阻止应用程序启动
            System.Diagnostics.Debug.WriteLine($"更新数据库架构时发生错误: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 重建 RetryRecords 表以应用字段长度限制
    /// </summary>
    private void RebuildRetryRecordsTable(System.Data.Common.DbConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            
            // 创建临时表
            command.CommandText = @"
                CREATE TABLE RetryRecords_temp (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Barcode TEXT(100) NOT NULL,
                    Company TEXT(50) NOT NULL,
                    RequestData TEXT(4000) NOT NULL,
                    CreateTime TEXT NOT NULL,
                    RetryTime TEXT,
                    LastRetryTime TEXT,
                    IsRetried INTEGER NOT NULL,
                    RetryCount INTEGER NOT NULL,
                    ErrorMessage TEXT(1000)
                )";
            command.ExecuteNonQuery();
            
            // 复制数据到临时表（截断过长的字段）
            command.CommandText = @"
                INSERT INTO RetryRecords_temp 
                SELECT 
                    Id,
                    SUBSTR(Barcode, 1, 100) as Barcode,
                    SUBSTR(Company, 1, 50) as Company,
                    SUBSTR(RequestData, 1, 4000) as RequestData,
                    CreateTime,
                    RetryTime,
                    LastRetryTime,
                    IsRetried,
                    RetryCount,
                    SUBSTR(COALESCE(ErrorMessage, ''), 1, 1000) as ErrorMessage
                FROM RetryRecords";
            command.ExecuteNonQuery();
            
            // 删除原表
            command.CommandText = "DROP TABLE RetryRecords";
            command.ExecuteNonQuery();
            
            // 重命名临时表
            command.CommandText = "ALTER TABLE RetryRecords_temp RENAME TO RetryRecords";
            command.ExecuteNonQuery();
            
            transaction.Commit();
            System.Diagnostics.Debug.WriteLine("成功更新 RetryRecords 表结构，应用了字段长度限制");
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public DbSet<RetryRecord> RetryRecords { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RetryRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Barcode).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Company).IsRequired().HasMaxLength(50);
            entity.Property(e => e.RequestData).IsRequired().HasMaxLength(4000);
            entity.Property(e => e.CreateTime).IsRequired();
            entity.Property(e => e.ErrorMessage).HasMaxLength(1000);
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
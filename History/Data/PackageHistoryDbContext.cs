using Microsoft.EntityFrameworkCore;

namespace History.Data;

/// <summary>
///     包裹历史数据库上下文
/// </summary>
public class PackageHistoryDbContext : DbContext
{
    private readonly DateTime _forDate; // 重命名

    /// <summary>
    ///     用于 PackageHistoryDataService 传递 Options
    /// </summary>
    public PackageHistoryDbContext(DbContextOptions<PackageHistoryDbContext> options)
        : base(options)
    {
        // 构造函数
        // 安全起见，假设 _forDate 是关键的，并且没有提供。
        _forDate = DateTime.Today; 
        // 日志由 Serilog 静态Log统一处理
    }

    /// <summary>
    ///     构造函数，允许服务传入特定的日期用于表名解析
    /// </summary>
    public PackageHistoryDbContext(DbContextOptions<PackageHistoryDbContext> options, DateTime? forDate)
        : base(options)
    {
        _forDate = forDate ?? DateTime.Today;
        // 日志由 Serilog 静态Log统一处理
    }

    /// <summary>
    ///     配置模型
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var tableName = $"Package_{_forDate:yyyyMM}";
        Serilog.Log.Information("PackageDbContext.OnModelCreating: Configuring entity for table {TableName}", tableName);

        modelBuilder.Entity<PackageHistoryRecord>(entity =>
        {
            entity.ToTable(tableName);
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();

            // 确保 PackageHistoryRecord 中的属性名称直接映射到列名称
            // 在 CreateMonthlyTableAsync 中除非显式配置否则。
            // entity.Property(e => e.Index).HasColumnName("PackageIndex"); // 移除：假设列是 "Index"
            entity.Property(e => e.Barcode).IsRequired(); 
            // 添加其他属性配置，例如字符串长度、IsRequired 等。
            // 例如：entity.Property(e => e.Barcode).HasMaxLength(100).IsRequired();
            // 明确配置 Status 属性为字符串，避免与同名枚举混淆
            entity.Property(e => e.Status).HasColumnType("TEXT"); // 根据需要指定 SQLite 列类型
            entity.Property(e => e.StatusDisplay).HasColumnType("TEXT"); // 根据需要指定 SQLite 列类型
            
            // 确保 PackageHistoryRecord 中的所有属性都被考虑。
            // 如果 CreateMonthlyTableAsync 有 PackageHistoryRecord 中没有的列，它们不会被 EF 管理。
            // 如果 PackageHistoryRecord 有 CreateMonthlyTableAsync 的 SQL 中没有的属性，EF 可能会尝试添加它们。
        });

        base.OnModelCreating(modelBuilder);
    }
} 
using Microsoft.EntityFrameworkCore;
using ILogger = Serilog.ILogger;

namespace History.Data;

/// <summary>
///     包裹历史数据库上下文
/// </summary>
public class PackageHistoryDbContext : DbContext
{
    private readonly DateTime _forDate; // 重命名
    public readonly ILogger? Logger; // 公开给服务层使用，用于 CreateMonthlyTableAsync 日志记录

    /// <summary>
    ///     用于 PackageHistoryDataService 传递 Options
    /// </summary>
    public PackageHistoryDbContext(DbContextOptions<PackageHistoryDbContext> options)
        : base(options)
    {
        // 构造函数
        // 安全起见，假设 _forDate 是关键的，并且没有提供。
        _forDate = DateTime.Today; 
        // Logger 在这里为 null，或者获取一个默认的空操作日志记录器。
    }

    /// <summary>
    ///     构造函数，允许服务传入特定的日期用于表名解析
    /// </summary>
    public PackageHistoryDbContext(DbContextOptions<PackageHistoryDbContext> options, DateTime? forDate, ILogger? logger)
        : base(options)
    {
        _forDate = forDate ?? DateTime.Today;
        Logger = logger; // Store the logger
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (Logger != null && optionsBuilder.IsConfigured == false) // 示例：仅在未由 DI 或测试配置时
        {
            // 如果使用 Microsoft.Extensions.Logging.ILoggerFactory 注册在 DI 中：
            // var loggerFactory = new LoggerFactory(); // 这是一个基本的设置
            // loggerFactory.AddProvider(new Serilog.Extensions.Logging.SerilogLoggerProvider(Logger)); // 如果 Logger 是 Serilog 的 ILogger
            // optionsBuilder.UseLoggerFactory(loggerFactory);
            // optionsBuilder.EnableSensitiveDataLogging(); // 用于调试，小心在生产中使用
        }
        base.OnConfiguring(optionsBuilder);
    }

    /// <summary>
    ///     配置模型
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var tableName = $"Package_{_forDate:yyyyMM}";
        Logger?.Information("PackageDbContext.OnModelCreating: Configuring entity for table {TableName}", tableName);

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
            // entity.Property(e => e.CreateTime).HasColumnType("TEXT"); // SQLite 特定，EF Core 通常处理它
            
            // 确保 PackageHistoryRecord 中的所有属性都被考虑。
            // 如果 CreateMonthlyTableAsync 有 PackageHistoryRecord 中没有的列，它们不会被 EF 管理。
            // 如果 PackageHistoryRecord 有 CreateMonthlyTableAsync 的 SQL 中没有的属性，EF 可能会尝试添加它们。
        });

        base.OnModelCreating(modelBuilder);
    }
} 
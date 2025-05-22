using History.Data;
using History.ViewModels.Dialogs;
using History.Views.Dialogs;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.IO;

namespace History
{
    public class HistoryModule : IModule
    {
        public void OnInitialized(IContainerProvider containerProvider)
        {
            try
            {
                // 尝试解析日志记录器。如果此处失败，则表明 ILogger 未在主应用程序中正确注册。
                var logger = containerProvider.Resolve<ILogger>();
                logger.Information("HistoryModule: OnInitialized - Module loaded.");

                // 自动初始化历史服务并执行数据库迁移
                var historyService = containerProvider.Resolve<IPackageHistoryDataService>();
                logger.Information("HistoryModule: 开始初始化历史服务并迁移旧库数据...");
                // 注意：此处为同步调用异步方法，仅适用于模块初始化阶段
                historyService.InitializeAsync().GetAwaiter().GetResult();
                logger.Information("HistoryModule: 历史服务初始化和迁移完成。");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HistoryModule: Critical error during OnInitialized. Failed to resolve ILogger or other setup issue. This indicates a DI registration problem in the main application. Exception: {ex.Message}");
                // 抛出异常或采取其他错误处理措施，因为模块可能无法正常运行。
                // throw new InvalidOperationException("HistoryModule could not be initialized due to missing ILogger registration or other critical error.", ex);
            }
        }

        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            var baseDataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            var dbPath = Path.Combine(baseDataDirectory, "Package.db");
            var connectionString = $"Data Source={dbPath}";

            var optionsBuilder = new DbContextOptionsBuilder<PackageHistoryDbContext>();
            optionsBuilder.UseSqlite(connectionString);
            containerRegistry.RegisterInstance(optionsBuilder.Options);

            containerRegistry.RegisterSingleton<IPackageHistoryDataService, PackageHistoryDataService>();
            containerRegistry.RegisterDialog<PackageHistoryDialogView, PackageHistoryDialogViewModel>();
        }
    }
} 
using History.Data;
using History.ViewModels.Dialogs;
using History.Views.Dialogs;
using Microsoft.EntityFrameworkCore;
using Prism.Ioc;
using Prism.Modularity;
using Serilog;
using System;
using System.IO;
using System.Threading.Tasks;

namespace History
{
    public class HistoryModule : IModule
    {
        public void OnInitialized(IContainerProvider containerProvider)
        {
            var historyService = containerProvider.Resolve<IPackageHistoryDataService>();
            var logger = containerProvider.Resolve<ILogger>();

            try
            {
                logger.Information("HistoryModule: Starting IPackageHistoryDataService.InitializeAsync() and waiting for completion...");
                historyService.InitializeAsync().GetAwaiter().GetResult();
                logger.Information("HistoryModule: IPackageHistoryDataService.InitializeAsync() completed successfully.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "HistoryModule: IPackageHistoryDataService.InitializeAsync() failed during synchronous wait.");
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
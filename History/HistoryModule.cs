using History.Data;
using History.ViewModels.Dialogs;
using History.Views.Dialogs;
using Microsoft.EntityFrameworkCore;
using System.IO;

namespace History
{
    public class HistoryModule : IModule
    {
        public void OnInitialized(IContainerProvider containerProvider)
        {
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
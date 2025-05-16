using System.Collections.ObjectModel;
using Common.Services.Settings;
using WeiCiModule.Models; 
using WeiCiModule.Models.Settings;
using Serilog;
using Microsoft.Win32;
using WeiCiModule.Services;
using System.Windows;

namespace WeiCiModule.ViewModels
{
    public class ChuteSettingsViewModel : BindableBase, IDialogAware
    {
        private readonly ISettingsService _settingsService;
        private readonly ExcelService _excelService;
        private ChuteSettings _chuteSettingsConfig;

        public string Title => "Library Branch Settings";

        public ObservableCollection<ChuteSettingItemViewModel?> ChuteSettingItems { get; } = new ObservableCollection<ChuteSettingItemViewModel?>();
        private ObservableCollection<string> GlobalAvailableBranchCodes { get; } = new ObservableCollection<string>();

        private ChuteSettingItemViewModel? _selectedChuteSettingItem;
        private DialogCloseListener _requestClose;

        public ChuteSettingItemViewModel? SelectedChuteSettingItem
        {
            get => _selectedChuteSettingItem;
            set => SetProperty(ref _selectedChuteSettingItem, value);
        }

        public DelegateCommand AddCommand { get; }
        public DelegateCommand RemoveCommand { get; }
        public DelegateCommand SaveCommand { get; }
        public DelegateCommand CloseCommand { get; }
        public DelegateCommand ImportExcelCommand { get; }
        public DelegateCommand ExportExcelCommand { get; }


        public ChuteSettingsViewModel(ISettingsService settingsService)
        {
            _settingsService = settingsService;
            _excelService = new ExcelService();
            _chuteSettingsConfig = new ChuteSettings(); // Initialize to avoid null
            _requestClose = new DialogCloseListener(); // Initialize DialogCloseListener

            // Initialize available branch codes
            InitializeAvailableBranchCodes();

            AddCommand = new DelegateCommand(ExecuteAdd);
            RemoveCommand = new DelegateCommand(ExecuteRemove, CanExecuteRemove)
                .ObservesProperty(() => SelectedChuteSettingItem);
            SaveCommand = new DelegateCommand(ExecuteSave);
            CloseCommand = new DelegateCommand(ExecuteClose);
            ImportExcelCommand = new DelegateCommand(ExecuteImportExcel);
            ExportExcelCommand = new DelegateCommand(ExecuteExportExcel, CanExecuteExportExcel)
                .ObservesProperty(() => ChuteSettingItems.Count);

            LoadSettings();
        }

        // Initialize available branch codes from table data
        private void InitializeAvailableBranchCodes()
        {
            GlobalAvailableBranchCodes.Clear();
            
            // Import branch codes from table
            var branchData = new List<(string code, string name)>
            {
                ("AMKPL", "Ang Mo Kio Public Library"),
                ("BBPL", "Bukit Batok Public Library (currently closed)"),
                ("BEPL", "Bedok Public Library"),
                ("BIPL", "Bishan Public Library"),
                ("BPPL", "Bukit Panjang Public Library"),
                ("CAL", "Central Arts Library (at NL Building)"),
                ("CCKPL", "Choa Chu Kang Public Library"),
                ("CPL", "Central Public Library"),
                ("CMPL", "Clementi Public Library"),
                ("CNPL", "library@chinatown"),
                ("CSPL", "Cheng San Public Library"),
                ("GEPL", "Geylang East Public Library"),
                ("HBPL", "library@harbourfront"),
                ("JRL", "Jurong Regional Library"),
                ("JWPL", "Jurong West Public Library"),
                ("LKCRL", "Lee Kong Chian Reference Library (at NL building)"),
                ("MOLLY", "Mobile Library"),
                ("MPPL", "Marine Parade Public Library (currently closed)"),
                ("OCPL", "library@orchard (currently closed)"),
                ("PRL", "Punggol Regional Library"),
                ("PRPL", "Pasir Ris Public Library"),
                ("QUPL", "Queenstown Public Library"),
                ("SBPL", "Sembawang Public Library"),
                ("SKPL", "Sengkang Public Library"),
                ("SRPL", "Serangoon Public Library"),
                ("TPPL", "Toa Payoh Public Library"),
                ("TRL", "Tampines Regional Library"),
                ("WRL", "Woodlands Regional Library"),
                ("YIPL", "Yishun Public Library")
            };

            // Add to available branch codes list
            foreach (var branch in branchData)
            {
                GlobalAvailableBranchCodes.Add(branch.code);
            }
        }

        // Import from Excel file
        private void ExecuteImportExcel()
        {
            try
            {
                // Create open file dialog
                var openFileDialog = new OpenFileDialog
                {
                    Filter = "Excel Files|*.xlsx;*.xls",
                    Title = "Select Excel File to Import"
                };

                // Show dialog and check if user selected a file
                if (openFileDialog.ShowDialog() != true)
                {
                    return;
                }

                // Use ExcelService to import data
                var importedData = _excelService.ImportFromExcel(openFileDialog.FileName);
                if (importedData.Count == 0)
                {
                    MessageBox.Show("No data was imported from the Excel file. Please check if the file format is correct.", "Import Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Confirm whether to replace existing data
                var result = MessageBox.Show($"Successfully read {importedData.Count} records from Excel. Replace all current data? Select \"No\" to append to existing data.", 
                    "Import Confirmation", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Cancel)
                {
                    return;
                }

                // Process data based on user choice
                if (result == MessageBoxResult.Yes)
                {
                    ChuteSettingItems.Clear();
                }

                // Add imported data
                foreach (var data in importedData)
                {
                    ChuteSettingItems.Add(new ChuteSettingItemViewModel(data, GlobalAvailableBranchCodes));
                }

                // Renumber items
                ReNumberItems();

                Log.Information("Successfully imported {Count} records from Excel", importedData.Count);
                MessageBox.Show($"Successfully imported {importedData.Count} records.", "Import Successful", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error importing Excel");
                MessageBox.Show($"Error importing Excel: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Export to Excel file
        private void ExecuteExportExcel()
        {
            try
            {
                // Create save file dialog
                var saveFileDialog = new SaveFileDialog
                {
                    Filter = "Excel Workbook|*.xlsx|Excel 97-2003 Workbook|*.xls",
                    DefaultExt = ".xlsx",
                    Title = "Save Excel File",
                    FileName = "LibraryBranchSettings_" + DateTime.Now.ToString("yyyyMMdd")
                };

                // Show dialog and check if user selected a save location
                if (saveFileDialog.ShowDialog() != true)
                {
                    return;
                }

                // Prepare data for export
                var dataToExport = ChuteSettingItems.Select(vm => vm?.ToData()).ToList();

                // Use ExcelService to export data
                bool success = _excelService.ExportToExcel(saveFileDialog.FileName, dataToExport);
                if (success)
                {
                    Log.Information("Successfully exported {Count} records to Excel: {FileName}", dataToExport.Count, saveFileDialog.FileName);
                    MessageBox.Show($"Successfully exported {dataToExport.Count} records to Excel file.", "Export Successful", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Failed to export Excel file. Please check the log for details.", "Export Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error exporting Excel");
                MessageBox.Show($"Error exporting Excel: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool CanExecuteExportExcel()
        {
            return ChuteSettingItems.Count > 0;
        }

        // Renumber all items
        private void ReNumberItems()
        {
            int sn = 1;
            foreach (var item in ChuteSettingItems)
            {
                if (item != null) item.SN = sn++;
            }
        }

        private void LoadSettings()
        {
            try
            {
                _chuteSettingsConfig = _settingsService.LoadSettings<ChuteSettings>();

                ChuteSettingItems.Clear();
                foreach (var itemData in _chuteSettingsConfig.Items.OrderBy(i => i.SN))
                {
                    ChuteSettingItems.Add(new ChuteSettingItemViewModel(itemData, GlobalAvailableBranchCodes));
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error loading branch settings.");
                _chuteSettingsConfig = new ChuteSettings(); // Fallback
            }
        }

        private void ExecuteAdd()
        {
            var newItemData = new ChuteSettingData
            {
                SN = (ChuteSettingItems.Any() ? ChuteSettingItems.Max(i => i!.SN) : 0) + 1,
                BranchCode = GlobalAvailableBranchCodes.FirstOrDefault() ?? string.Empty, // Default to first available branch code
                Branch = string.Empty
            };
            var newItemViewModel = new ChuteSettingItemViewModel(newItemData, GlobalAvailableBranchCodes);
            ChuteSettingItems.Add(newItemViewModel);
            SelectedChuteSettingItem = newItemViewModel; // Select new item
        }

        private void ExecuteRemove()
        {
            ChuteSettingItems.Remove(SelectedChuteSettingItem);
        }

        private bool CanExecuteRemove()
        {
            return SelectedChuteSettingItem != null;
        }

        private void ExecuteSave()
        {
            try
            {
                _chuteSettingsConfig.Items.Clear();
                int currentSn = 1;
                foreach (var itemVm in ChuteSettingItems)
                {
                    itemVm!.SN = currentSn++;
                    _chuteSettingsConfig.Items.Add(itemVm.ToData());
                }
                _settingsService.SaveSettings(_chuteSettingsConfig);
                
                Log.Information("Branch settings successfully saved.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error saving branch settings. Handling approach: Log error, keep dialog open.");
            }
        }

        private void ExecuteClose()
        {
            _requestClose.Invoke(new DialogResult(ButtonResult.Cancel));
        }

        public bool CanCloseDialog() => true;

        public void OnDialogClosed() { }

        public void OnDialogOpened(IDialogParameters parameters)
        {
            LoadSettings(); // Reload or refresh settings
        }

        DialogCloseListener IDialogAware.RequestClose => _requestClose;
    }
}
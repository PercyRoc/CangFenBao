using System.Collections.ObjectModel;
using System.Windows.Input;
using Serilog;

namespace ChileSowing.ViewModels;

public class ChuteDetailDialogViewModel : BindableBase, IDialogAware
{
    private string _title = null!;
    private ObservableCollection<ChutePackageItem>? _skus;

    public ChuteDetailDialogViewModel()
    {
        CloseCommand = new DelegateCommand(ExecuteClose);
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public ObservableCollection<ChutePackageItem>? Skus
    {
        get => _skus;
        private set => SetProperty(ref _skus, value);
    }

    public ICommand CloseCommand { get; }

    public DialogCloseListener RequestClose { get; } = new();

    public bool CanCloseDialog() => true;
    public void OnDialogClosed() { }
    public void OnDialogOpened(IDialogParameters parameters)
    {
        if (parameters.ContainsKey("title"))
            Title = parameters.GetValue<string>("title");
        if (!parameters.ContainsKey("skus")) return;
        var receivedSkus = parameters.GetValue<ObservableCollection<ChutePackageItem>>("skus");
        if (Skus == null)
        {
            Skus = [];
        }
        else
        {
            Skus.Clear();
        }

        foreach(var item in receivedSkus)
        {
            Skus.Add(item);
            Log.Information("- SKU: {ItemSku} at {ItemTimestamp}", item.Sku, item.Timestamp);
        }
        Log.Information("对话框打开，接收到 {ReceivedSkusCount} 个 SKU。", receivedSkus?.Count ?? 0);
    }

    private void ExecuteClose()
    {
        RequestClose.Invoke(new DialogResult(ButtonResult.Cancel));
    }
} 
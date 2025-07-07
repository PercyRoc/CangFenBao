using System.Collections.ObjectModel;
using Serilog;
using XinJuLi.Models.ASN;
using XinJuLi.Services.ASN;

namespace XinJuLi.ViewModels.Dialogs;

/// <summary>
///     ASN单选择对话框视图模型
/// </summary>
public class AsnOrderSelectionDialogViewModel : BindableBase, IDialogAware
{
    private readonly IAsnCacheService _asnCacheService;
    private readonly IAsnStorageService _asnStorageService;
    private bool _isLoading;
    private string _newAsnOrderCode = string.Empty;
    private string _searchText = string.Empty;
    private AsnOrderInfo? _selectedAsnOrder;
    private string _title = "选择ASN单";

    /// <summary>
    ///     构造函数
    /// </summary>
    public AsnOrderSelectionDialogViewModel(
        IAsnCacheService asnCacheService,
        IAsnStorageService asnStorageService)
    {
        _asnCacheService = asnCacheService;
        _asnStorageService = asnStorageService;

        AsnOrders = [];

        // 初始化命令
        ConfirmCommand = new DelegateCommand(ExecuteConfirm, CanExecuteConfirm);
        CancelCommand = new DelegateCommand(ExecuteCancel);
        RefreshCommand = new DelegateCommand(ExecuteRefresh);
        RemoveSelectedCommand = new DelegateCommand(ExecuteRemoveSelected, CanExecuteRemoveSelected);

        // 订阅缓存变更事件
        _asnCacheService.CacheChanged += OnCacheChanged;

        // 加载ASN单
        LoadAsnOrders();
    }

    /// <summary>
    ///     ASN单列表
    /// </summary>
    public ObservableCollection<AsnOrderInfo> AsnOrders { get; }

    /// <summary>
    ///     选中的ASN单
    /// </summary>
    public AsnOrderInfo? SelectedAsnOrder
    {
        get => _selectedAsnOrder;
        set
        {
            if (SetProperty(ref _selectedAsnOrder, value))
            {
                ConfirmCommand.RaiseCanExecuteChanged();
                RemoveSelectedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    ///     搜索文本
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                FilterAsnOrders();
            }
        }
    }

    /// <summary>
    ///     新收到的ASN单编码（用于高亮显示）
    /// </summary>
    public string NewAsnOrderCode
    {
        get => _newAsnOrderCode;
        set => SetProperty(ref _newAsnOrderCode, value);
    }

    /// <summary>
    ///     是否正在加载
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    /// <summary>
    ///     确认命令
    /// </summary>
    public DelegateCommand ConfirmCommand { get; }

    /// <summary>
    ///     取消命令
    /// </summary>
    public DelegateCommand CancelCommand { get; }

    /// <summary>
    ///     刷新命令
    /// </summary>
    public DelegateCommand RefreshCommand { get; }

    /// <summary>
    ///     移除选中命令
    /// </summary>
    public DelegateCommand RemoveSelectedCommand { get; }

    /// <summary>
    ///     对话框标题
    /// </summary>
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>
    ///     请求关闭事件
    /// </summary>
    public DialogCloseListener RequestClose { get; } = default!;

    /// <summary>
    ///     是否可以关闭对话框
    /// </summary>
    public bool CanCloseDialog()
    {
        return true;
    }

    /// <summary>
    ///     对话框关闭时
    /// </summary>
    public void OnDialogClosed()
    {
        // 取消订阅事件
        _asnCacheService.CacheChanged -= OnCacheChanged;
    }

    /// <summary>
    ///     对话框打开时
    /// </summary>
    public void OnDialogOpened(IDialogParameters parameters)
    {
        if (parameters.TryGetValue<string>("title", out var title))
        {
            Title = title;
        }

        if (parameters.TryGetValue<string>("NewAsnOrderCode", out var newAsnOrderCode))
        {
            NewAsnOrderCode = newAsnOrderCode;
        }

        // 加载ASN单
        LoadAsnOrders();
    }

    /// <summary>
    ///     加载ASN单列表
    /// </summary>
    private void LoadAsnOrders()
    {
        try
        {
            IsLoading = true;
            AsnOrders.Clear();

            // 从缓存加载
            var cachedOrders = _asnCacheService.GetAllAsnOrders();
            foreach (var order in cachedOrders)
            {
                order.IsNewReceived = true;
                AsnOrders.Add(order);
            }

            // 从存储加载
            var storedOrders = _asnStorageService.GetAllAsnOrders();
            foreach (var order in storedOrders.Where(order => cachedOrders.All(x => x.OrderCode != order.OrderCode)))
            {
                AsnOrders.Add(order);
            }

            // 应用搜索过滤
            FilterAsnOrders();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载ASN单列表失败");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    ///     过滤ASN单
    /// </summary>
    private void FilterAsnOrders()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            // 显示所有ASN单
            foreach (var order in AsnOrders)
            {
                order.IsVisible = true;
            }
        }
        else
        {
            // 根据搜索文本过滤
            var searchText = SearchText.ToLowerInvariant();
            foreach (var order in AsnOrders)
            {
                order.IsVisible = order.OrderCode.Contains(searchText, StringComparison.InvariantCultureIgnoreCase) ||
                                  order.CarCode.Contains(searchText, StringComparison.InvariantCultureIgnoreCase);
            }
        }
    }

    /// <summary>
    ///     缓存变更事件处理
    /// </summary>
    private void OnCacheChanged(object? sender, AsnCacheChangedEventArgs e)
    {
        switch (e.Action)
        {
            case "Added":
                if (e.AsnOrderInfo != null)
                {
                    e.AsnOrderInfo.IsNewReceived = true;
                    AsnOrders.Insert(0, e.AsnOrderInfo);
                    NewAsnOrderCode = e.AsnOrderInfo.OrderCode;
                }
                break;

            case "Removed":
                if (e.AsnOrderInfo != null)
                {
                    var order = AsnOrders.FirstOrDefault(x => x.OrderCode == e.AsnOrderInfo.OrderCode);
                    if (order != null)
                    {
                        AsnOrders.Remove(order);
                    }
                }
                break;

            case "Cleared":
                AsnOrders.Clear();
                LoadAsnOrders();
                break;
        }
    }

    /// <summary>
    ///     执行确认
    /// </summary>
    private void ExecuteConfirm()
    {
        if (SelectedAsnOrder == null) return;
        var parameters = new DialogParameters
        {
            {
                "selectedAsnOrder", SelectedAsnOrder
            }
        };

        var result = new DialogResult(ButtonResult.OK)
        {
            Parameters = parameters
        };
        RequestClose.Invoke(result);
    }

    /// <summary>
    ///     是否可以执行确认
    /// </summary>
    private bool CanExecuteConfirm()
    {
        return SelectedAsnOrder != null;
    }

    /// <summary>
    ///     执行取消
    /// </summary>
    private void ExecuteCancel()
    {
        RequestClose.Invoke(new DialogResult(ButtonResult.Cancel));
    }

    /// <summary>
    ///     执行刷新
    /// </summary>
    private void ExecuteRefresh()
    {
        LoadAsnOrders();
    }

    /// <summary>
    ///     执行移除选中
    /// </summary>
    private void ExecuteRemoveSelected()
    {
        if (SelectedAsnOrder == null) return;
        // 从缓存中移除
        _asnCacheService.RemoveAsnOrder(SelectedAsnOrder.OrderCode);

        // 从存储中移除
        _asnStorageService.DeleteAsnOrder(SelectedAsnOrder.OrderCode);

        // 从列表中移除
        AsnOrders.Remove(SelectedAsnOrder);
    }

    /// <summary>
    ///     是否可以执行移除选中
    /// </summary>
    private bool CanExecuteRemoveSelected()
    {
        return SelectedAsnOrder != null;
    }
}
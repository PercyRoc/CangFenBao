using System.Collections.ObjectModel;
using System.Windows;
using Serilog;
using XinJuLi.Models.ASN;
using XinJuLi.Services.ASN;

namespace XinJuLi.ViewModels.Dialogs
{
    /// <summary>
    /// ASN单选择对话框视图模型
    /// </summary>
    public class AsnOrderSelectionDialogViewModel : BindableBase, IDialogAware
    {
        private readonly IAsnCacheService _asnCacheService;
        private AsnOrderInfo? _selectedAsnOrder;
        private string _searchText = string.Empty;
        private string _newAsnOrderCode = string.Empty;

        /// <summary>
        /// 构造函数
        /// </summary>
        public AsnOrderSelectionDialogViewModel(IAsnCacheService asnCacheService)
        {
            _asnCacheService = asnCacheService;
            ConfirmCommand = new DelegateCommand(ExecuteConfirm, CanExecuteConfirm);
            CancelCommand = new DelegateCommand(ExecuteCancel);
            RefreshCommand = new DelegateCommand(ExecuteRefresh);
            RemoveSelectedCommand = new DelegateCommand(ExecuteRemoveSelected, CanExecuteRemoveSelected);
            
            AsnOrders = new ObservableCollection<AsnOrderInfo>();
            
            // 订阅缓存变更事件
            _asnCacheService.CacheChanged += OnCacheChanged;
            
            LoadAsnOrders();
        }

        /// <summary>
        /// ASN单列表
        /// </summary>
        public ObservableCollection<AsnOrderInfo> AsnOrders { get; }

        /// <summary>
        /// 选中的ASN单
        /// </summary>
        public AsnOrderInfo? SelectedAsnOrder
        {
            get => _selectedAsnOrder;
            set
            {
                SetProperty(ref _selectedAsnOrder, value);
                ConfirmCommand.RaiseCanExecuteChanged();
                RemoveSelectedCommand.RaiseCanExecuteChanged();
            }
        }

        /// <summary>
        /// 搜索文本
        /// </summary>
        public string SearchText
        {
            get => _searchText;
            set
            {
                SetProperty(ref _searchText, value);
                FilterAsnOrders();
            }
        }

        /// <summary>
        /// 新收到的ASN单编码（用于高亮显示）
        /// </summary>
        public string NewAsnOrderCode
        {
            get => _newAsnOrderCode;
            private set => SetProperty(ref _newAsnOrderCode, value);
        }

        /// <summary>
        /// 确认命令
        /// </summary>
        public DelegateCommand ConfirmCommand { get; }

        /// <summary>
        /// 取消命令
        /// </summary>
        public DelegateCommand CancelCommand { get; }

        /// <summary>
        /// 刷新命令
        /// </summary>
        public DelegateCommand RefreshCommand { get; }

        /// <summary>
        /// 移除选中命令
        /// </summary>
        public DelegateCommand RemoveSelectedCommand { get; }

        /// <summary>
        /// 对话框标题
        /// </summary>
        public string Title => "选择ASN单";

        /// <summary>
        /// 请求关闭事件
        /// </summary>
        public DialogCloseListener RequestClose { get; private set; } = default!;

        /// <summary>
        /// 是否可以关闭对话框
        /// </summary>
        public bool CanCloseDialog()
        {
            return true;
        }

        /// <summary>
        /// 对话框关闭时
        /// </summary>
        public void OnDialogClosed()
        {
            // 取消订阅事件
            _asnCacheService.CacheChanged -= OnCacheChanged;
        }

        /// <summary>
        /// 对话框打开时
        /// </summary>
        public void OnDialogOpened(IDialogParameters parameters)
        {
            // 获取新收到的ASN单编码
            if (parameters.ContainsKey("NewAsnOrderCode"))
            {
                NewAsnOrderCode = parameters.GetValue<string>("NewAsnOrderCode");
                Log.Information("收到新的ASN单编码用于高亮显示: {OrderCode}", NewAsnOrderCode);
            }
            
            LoadAsnOrders();
            
            // 标记新收到的ASN单并自动选中
            if (!string.IsNullOrEmpty(NewAsnOrderCode))
            {
                foreach (var order in AsnOrders)
                {
                    order.IsNewReceived = order.OrderCode == NewAsnOrderCode;
                }
                
                var newAsnOrder = AsnOrders.FirstOrDefault(x => x.OrderCode == NewAsnOrderCode);
                if (newAsnOrder != null)
                {
                    SelectedAsnOrder = newAsnOrder;
                }
            }
        }

        /// <summary>
        /// 加载ASN单列表
        /// </summary>
        private void LoadAsnOrders()
        {
            try
            {
                var asnOrders = _asnCacheService.GetAllAsnOrders();
                
                AsnOrders.Clear();
                foreach (var asnOrder in asnOrders)
                {
                    AsnOrders.Add(asnOrder);
                }

                Log.Information("已加载{Count}个缓存的ASN单", AsnOrders.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "加载ASN单列表时发生错误");
            }
        }

        /// <summary>
        /// 过滤ASN单
        /// </summary>
        private void FilterAsnOrders()
        {
            try
            {
                var allOrders = _asnCacheService.GetAllAsnOrders();
                
                var filteredOrders = string.IsNullOrWhiteSpace(SearchText)
                    ? allOrders
                    : allOrders.Where(x => 
                        x.OrderCode.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                        x.CarCode.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                AsnOrders.Clear();
                foreach (var order in filteredOrders)
                {
                    AsnOrders.Add(order);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "过滤ASN单时发生错误");
            }
        }

        /// <summary>
        /// 缓存变更事件处理
        /// </summary>
        private void OnCacheChanged(object? sender, AsnCacheChangedEventArgs e)
        {
            // 在UI线程中更新
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                LoadAsnOrders();
            });
        }

        /// <summary>
        /// 执行确认
        /// </summary>
        private void ExecuteConfirm()
        {
            if (SelectedAsnOrder == null) return;

            var parameters = new DialogParameters
            {
                { "SelectedAsnOrder", SelectedAsnOrder }
            };

            RequestClose.Invoke(new DialogResult(ButtonResult.OK) { Parameters = parameters });
        }

        /// <summary>
        /// 是否可以执行确认
        /// </summary>
        private bool CanExecuteConfirm()
        {
            return SelectedAsnOrder != null;
        }

        /// <summary>
        /// 执行取消
        /// </summary>
        private void ExecuteCancel()
        {
            RequestClose.Invoke(new DialogResult(ButtonResult.Cancel));
        }

        /// <summary>
        /// 执行刷新
        /// </summary>
        private void ExecuteRefresh()
        {
            LoadAsnOrders();
        }

        /// <summary>
        /// 执行移除选中
        /// </summary>
        private void ExecuteRemoveSelected()
        {
            if (SelectedAsnOrder == null) return;

            _asnCacheService.RemoveAsnOrder(SelectedAsnOrder.OrderCode);
            SelectedAsnOrder = null;
        }

        /// <summary>
        /// 是否可以执行移除选中
        /// </summary>
        private bool CanExecuteRemoveSelected()
        {
            return SelectedAsnOrder != null;
        }
    }
} 
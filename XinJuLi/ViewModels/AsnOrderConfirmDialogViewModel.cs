using XinJuLi.Models.ASN;

namespace XinJuLi.ViewModels
{
    /// <summary>
    /// ASN订单确认对话框视图模型
    /// </summary>
    public class AsnOrderConfirmDialogViewModel : BindableBase, IDialogAware
    {
        private AsnOrderInfo _asnOrderInfo = new();
        private string _orderCode = string.Empty;
        private string _carCode = string.Empty;
        private int _itemsCount;

        /// <summary>
        /// 确认命令
        /// </summary>
        public DelegateCommand ConfirmCommand { get; }
        
        /// <summary>
        /// 取消命令
        /// </summary>
        public DelegateCommand CancelCommand { get; }

        /// <summary>
        /// 构造函数
        /// </summary>
        public AsnOrderConfirmDialogViewModel()
        {
            ConfirmCommand = new DelegateCommand(ExecuteConfirm);
            CancelCommand = new DelegateCommand(ExecuteCancel);
        }

        /// <summary>
        /// ASN单编码
        /// </summary>
        public string OrderCode
        {
            get => _orderCode;
            private set => SetProperty(ref _orderCode, value);
        }

        /// <summary>
        /// 车牌号
        /// </summary>
        public string CarCode
        {
            get => _carCode;
            private set => SetProperty(ref _carCode, value);
        }

        /// <summary>
        /// 货品数量
        /// </summary>
        public int ItemsCount
        {
            get => _itemsCount;
            private set => SetProperty(ref _itemsCount, value);
        }

        /// <summary>
        /// 对话框标题
        /// </summary>
        public string Title => "确认ASN单信息";

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
        }

        /// <summary>
        /// 对话框打开时
        /// </summary>
        public void OnDialogOpened(IDialogParameters parameters)
        {
            if (!parameters.ContainsKey("AsnOrderInfo")) return;
            _asnOrderInfo = parameters.GetValue<AsnOrderInfo>("AsnOrderInfo");
            OrderCode = _asnOrderInfo.OrderCode;
            CarCode = _asnOrderInfo.CarCode;
            ItemsCount = _asnOrderInfo.Items.Count;
        }

        /// <summary>
        /// 执行确认
        /// </summary>
        private void ExecuteConfirm()
        {
            RequestClose.Invoke(new DialogResult(ButtonResult.OK));
        }

        /// <summary>
        /// 执行取消
        /// </summary>
        private void ExecuteCancel()
        {
            RequestClose.Invoke(new DialogResult(ButtonResult.Cancel));
        }
    }
} 
using System.Collections.ObjectModel;
using System;

namespace ChileSowing.ViewModels
{
    // New model to hold SKU and timestamp
    public class ChutePackageItem(string sku, DateTime timestamp)
    {
        public string Sku { get; set; } = sku;
        public DateTime Timestamp { get; set; } = timestamp;
    }

    public class ChuteViewModel(int chuteNumber) : BindableBase
    {
        private int _chuteNumber = chuteNumber;
        private string _sku = "SKU...";
        private string _quantity = "0/0";
        private bool _isTargetChute;
        private bool _isErrorState;
        private string _statusColor = "#FFFFFF";

        public int ChuteNumber
        {
            get => _chuteNumber;
            set => SetProperty(ref _chuteNumber, value);
        }

        public string Sku
        {
            get => _sku;
            set => SetProperty(ref _sku, value);
        }

        public string Quantity
        {
            get => _quantity;
            set => SetProperty(ref _quantity, value);
        }

        public bool IsTargetChute
        {
            get => _isTargetChute;
            set => SetProperty(ref _isTargetChute, value);
        }

        public bool IsErrorState
        {
            get => _isErrorState;
            set => SetProperty(ref _isErrorState, value);
        }

        public string StatusColor
        {
            get => _statusColor;
            set => SetProperty(ref _statusColor, value);
        }

        public ObservableCollection<ChutePackageItem> Skus { get; } = new();

        public void AddSku(string sku)
        {
            Skus.Add(new ChutePackageItem(sku, DateTime.Now));
            Sku = sku;
            Quantity = $"{Skus.Count}/0";
        }
    }
} 
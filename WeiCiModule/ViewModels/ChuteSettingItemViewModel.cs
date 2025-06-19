using System.Collections.ObjectModel;
using WeiCiModule.Models; // 引用 ChuteSettingData

namespace WeiCiModule.ViewModels
{
    public class ChuteSettingItemViewModel : BindableBase
    {
        private int _sn;
        public int SN
        {
            get => _sn;
            set => SetProperty(ref _sn, value);
        }

        private string _branchCode = string.Empty;
        public string BranchCode
        {
            get => _branchCode;
            set => SetProperty(ref _branchCode, value);
        }

        private string _branch = string.Empty;
        public string Branch
        {
            get => _branch;
            set => SetProperty(ref _branch, value);
        }

        // 这个列表将由父ViewModel (ChuteSettingsViewModel) 填充和拥有
        public ObservableCollection<string> AvailableBranchCodes { get; }

        public ChuteSettingItemViewModel()
        {
            // 在设计时或特殊情况下可能需要一个空的列表
            AvailableBranchCodes = [];
        }

        // 从数据模型映射的构造函数
        public ChuteSettingItemViewModel(ChuteSettingData data, ObservableCollection<string> globalBranchCodesSource)
        {
            SN = data.SN;
            BranchCode = data.BranchCode;
            Branch = data.Branch;

            // 直接引用父ViewModel的全局列表，而不是复制
            AvailableBranchCodes = globalBranchCodesSource;
        }

        public ChuteSettingData ToData()
        {
            return new ChuteSettingData
            {
                SN = this.SN,
                BranchCode = this.BranchCode,
                Branch = this.Branch
            };
        }
    }
} 
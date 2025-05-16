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

        // 这个列表将由父ViewModel (ChuteSettingsViewModel) 填充
        public ObservableCollection<string> AvailableBranchCodes { get; } = new ObservableCollection<string>();

        public ChuteSettingItemViewModel()
        {
        }

        // 从数据模型映射的构造函数
        public ChuteSettingItemViewModel(ChuteSettingData data, ObservableCollection<string>? globalBranchCodesSource)
        {
            SN = data.SN;
            BranchCode = data.BranchCode;
            Branch = data.Branch;
            
            // 为每个项复制一份全局可用格口列表
            if (globalBranchCodesSource != null)
            {
                foreach (var code in globalBranchCodesSource)
                {
                    AvailableBranchCodes.Add(code);
                }
            }

            // 如果已保存的BranchCode不在当前可用列表中，并且不为空，则添加进去，以防数据显示问题。
            if (!string.IsNullOrEmpty(BranchCode) && !AvailableBranchCodes.Contains(BranchCode))
            {
                AvailableBranchCodes.Add(BranchCode);
            }
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
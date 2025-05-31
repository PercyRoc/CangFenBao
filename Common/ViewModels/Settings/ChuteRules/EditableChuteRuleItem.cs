using Common.Models.Settings.ChuteRules;
using Prism.Mvvm;

namespace Common.ViewModels.Settings.ChuteRules
{
    public class EditableChuteRuleItem : BindableBase
    {
        private int _chuteNumber;
        public int ChuteNumber
        {
            get => _chuteNumber;
            set => SetProperty(ref _chuteNumber, value);
        }

        private BarcodeMatchRule _rule;
        public BarcodeMatchRule Rule
        {
            get => _rule;
            set => SetProperty(ref _rule, value);
        }

        public EditableChuteRuleItem(int chuteNumber, BarcodeMatchRule rule)
        {
            _chuteNumber = chuteNumber;
            _rule = rule;
        }
    }
} 
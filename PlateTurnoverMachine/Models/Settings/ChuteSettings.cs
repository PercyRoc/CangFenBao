using Common.Services.Settings;
using Prism.Mvvm;

namespace PlateTurnoverMachine.Models.Settings;

[Configuration("ChuteSettings")]
public class ChuteSettings : BindableBase
{
    private int _chuteCount = 1;
    private int _errorChute;

    public int ChuteCount
    {
        get => _chuteCount;
        set => SetProperty(ref _chuteCount, value);
    }

    public int ErrorChute
    {
        get => _errorChute;
        set => SetProperty(ref _errorChute, value);
    }
} 
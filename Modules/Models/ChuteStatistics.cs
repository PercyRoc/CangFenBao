using Prism.Mvvm;

namespace ShanghaiModuleBelt.Models;

/// <summary>
///     格口统计信息模型
/// </summary>
public class ChuteStatistics : BindableBase
{
    private int _chuteNumber;
    private int _packageCount;

    /// <summary>
    ///     格口号
    /// </summary>
    public int ChuteNumber
    {
        get => _chuteNumber;
        set => SetProperty(ref _chuteNumber, value);
    }

    /// <summary>
    ///     包裹总数
    /// </summary>
    public int PackageCount
    {
        get { return _packageCount; }
        set => SetProperty(ref _packageCount, value);
    }
}
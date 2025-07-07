namespace Camera.Models.Settings;

/// <summary>
/// 条码重复设置
/// </summary>
public class BarcodeDuplicationSettings : BindableBase
{
    private int _filterCount = 100; // Check last 100 barcodes
    private int _duplicationTimeMs = 5000; // 5 seconds

    /// <summary>
    /// 条码过滤的数量 (检查最近多少个条码)
    /// </summary>
    public int FilterCount
    {
        get => _filterCount;
        set => SetProperty(ref _filterCount, value);
    }

    /// <summary>
    /// 重复时间窗口 (毫秒)
    /// </summary>
    public int DuplicationTimeMs
    {
        get => _duplicationTimeMs;
        set => SetProperty(ref _duplicationTimeMs, value);
    }
} 
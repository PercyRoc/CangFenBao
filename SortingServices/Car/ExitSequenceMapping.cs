namespace SortingServices.Car;

/// <summary>
/// 格口与小车序列的对应关系
/// </summary>
public class ExitSequenceMapping : BindableBase
{
    private List<SortingSequence> _sequences = [];

    /// <summary>
    ///     格口号
    /// </summary>
    public int Exit { get; set; }

    /// <summary>
    ///     分拣序列
    /// </summary>
    public List<SortingSequence> Sequences
    {
        get => _sequences;
        set => SetProperty(ref _sequences, value);
    }
} 
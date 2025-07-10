namespace Common.Events;

/// <summary>
/// 分拣光电信号事件
/// </summary>
public class SortingSignalEvent : PubSubEvent<(string PhotoelectricName, DateTime SignalTime)>
{
} 
using Prism.Events;

namespace Common.Events;

/// <summary>
///     包裹处理事件
/// </summary>
public class PackageProcessingEvent : PubSubEvent<DateTime>
{
}
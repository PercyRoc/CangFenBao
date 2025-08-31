using DongtaiFlippingBoardMachine.Models;
using Prism.Events;

namespace DongtaiFlippingBoardMachine.Events;

/// <summary>
///     当翻板机设置在UI中被更改时发布的事件。
/// </summary>
public class PlateTurnoverSettingsUpdatedEvent : PubSubEvent<PlateTurnoverSettings>
{
}
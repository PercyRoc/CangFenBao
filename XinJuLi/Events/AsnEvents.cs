using XinJuLi.Models.ASN;

namespace XinJuLi.Events
{
    /// <summary>
    /// 当ASN单数据接收并确认后发布的事件
    /// </summary>
    public class AsnOrderReceivedEvent : PubSubEvent<AsnOrderInfo>
    {
    }

    /// <summary>
    /// 当ASN单数据被添加到缓存时发布此事件。
    /// </summary>
    public class AsnOrderAddedToCacheEvent : PubSubEvent<AsnOrderInfo>
    {
    }
} 
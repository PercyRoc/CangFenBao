using JetBrains.Annotations;
using Newtonsoft.Json;

namespace FuzhouPolicyForce.Models.AnttoWeight;

public class AnttoWeightResponse
{
    public string Code { get; set; } = null!;
    public string Msg { get; set; } = null!;
    public string ErrMsg { [UsedImplicitly] get; set; } = null!;

    [JsonConverter(typeof(AnttoWeightTimestampConverter))]
    public DateTime Timestamp { get; set; }

    public AnttoWeightResponseData Data { get; set; } = null!;
}

public class AnttoWeightResponseData
{
    [UsedImplicitly] public string CarrierCode { get; set; } = null!;

    [UsedImplicitly] public string ProvinceName { get; set; } = null!;
}

/// <summary>
///     自定义JSON转换器，用于处理安通API返回的特殊时间格式
///     格式: "2025-07-07 16:37:59:890" (毫秒用冒号分隔)
/// </summary>
public class AnttoWeightTimestampConverter : JsonConverter<DateTime>
{
    public override DateTime ReadJson(JsonReader reader, Type objectType, DateTime existingValue, bool hasExisting,
        JsonSerializer serializer)
    {
        if (reader.TokenType != JsonToken.String) return DateTime.MinValue;
        var timestampString = reader.Value?.ToString();
        if (string.IsNullOrEmpty(timestampString))
            return DateTime.MinValue;

        // 处理安通API的特殊时间格式: "2025-07-07 16:37:59:890"
        // 将最后一个冒号替换为点号，使其符合标准格式
        var lastColonIndex = timestampString.LastIndexOf(':');
        if (lastColonIndex > 0)
        {
            var correctedTimestamp = timestampString.Substring(0, lastColonIndex) + "." +
                                     timestampString.Substring(lastColonIndex + 1);
            if (DateTime.TryParse(correctedTimestamp, out var result))
                return result;
        }

        // 如果无法解析，尝试直接解析原始字符串
        if (DateTime.TryParse(timestampString, out var fallbackResult))
            return fallbackResult;

        return DateTime.MinValue;
    }

    public override void WriteJson(JsonWriter writer, DateTime value, JsonSerializer serializer)
    {
        writer.WriteValue(value.ToString("yyyy-MM-dd HH:mm:ss.fff"));
    }
}
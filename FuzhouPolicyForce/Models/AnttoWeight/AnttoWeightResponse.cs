using System;
using JetBrains.Annotations;
using Newtonsoft.Json;

namespace FuzhouPolicyForce.Models.AnttoWeight
{
    public class AnttoWeightResponse
    {
        public string Code { get; set; }
        public string Msg { get; set; }
        [CanBeNull] public string ErrMsg { get; set; }
        
        [JsonConverter(typeof(AnttoWeightTimestampConverter))]
        public DateTime Timestamp { get; set; }
        
        [CanBeNull] public AnttoWeightResponseData Data { get; set; }
    }

    public class AnttoWeightResponseData
    {
        public string CarrierCode { get; set; }
        public string ProvinceName { get; set; }
    }

    /// <summary>
    /// 自定义JSON转换器，用于处理安通API返回的特殊时间格式
    /// 格式: "2025-07-07 16:37:59:890" (毫秒用冒号分隔)
    /// </summary>
    public class AnttoWeightTimestampConverter : JsonConverter<DateTime>
    {
        public override DateTime ReadJson(JsonReader reader, Type objectType, DateTime existingValue, bool hasExisting, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.String)
            {
                var timestampString = reader.Value?.ToString();
                if (string.IsNullOrEmpty(timestampString))
                    return DateTime.MinValue;

                // 处理安通API的特殊时间格式: "2025-07-07 16:37:59:890"
                // 将最后一个冒号替换为点号，使其符合标准格式
                var lastColonIndex = timestampString.LastIndexOf(':');
                if (lastColonIndex > 0)
                {
                    var correctedTimestamp = timestampString.Substring(0, lastColonIndex) + "." + timestampString.Substring(lastColonIndex + 1);
                    if (DateTime.TryParse(correctedTimestamp, out var result))
                        return result;
                }

                // 如果无法解析，尝试直接解析原始字符串
                if (DateTime.TryParse(timestampString, out var fallbackResult))
                    return fallbackResult;
            }

            return DateTime.MinValue;
        }

        public override void WriteJson(JsonWriter writer, DateTime value, JsonSerializer serializer)
        {
            writer.WriteValue(value.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        }
    }
}
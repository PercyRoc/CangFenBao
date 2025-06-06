using System.Text.Json;
using System.Text.Json.Serialization;

namespace FuzhouPolicyForce.Models
{
    /// <summary>
    /// 字符串到布尔值的JSON转换器
    /// 处理API返回"true"/"false"字符串而不是布尔值的情况
    /// </summary>
    public class StringToBoolConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.True)
                return true;
            
            if (reader.TokenType == JsonTokenType.False)
                return false;
            
            if (reader.TokenType == JsonTokenType.String)
            {
                var stringValue = reader.GetString();
                if (bool.TryParse(stringValue, out var boolValue))
                    return boolValue;
                    
                // 处理"1"/"0"、"yes"/"no"等其他常见格式
                return stringValue?.ToLowerInvariant() switch
                {
                    "1" or "yes" or "y" or "on" => true,
                    "0" or "no" or "n" or "off" => false,
                    _ => false // 默认为false
                };
            }
            
            if (reader.TokenType == JsonTokenType.Number)
            {
                return reader.GetInt32() != 0;
            }
            
            return false; // 默认为false
        }

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        {
            writer.WriteBooleanValue(value);
        }
    }

    public class ShenTongLanShouResponse
    {
        [JsonPropertyName("success")]
        [JsonConverter(typeof(StringToBoolConverter))]
        public bool Success { get; set; }

        [JsonPropertyName("errorMsg")]
        public string? ErrorMsg { get; set; }

        [JsonPropertyName("errorCode")]
        public string? ErrorCode { get; set; }

        [JsonPropertyName("data")]
        public string? Data { get; set; }
    }
} 
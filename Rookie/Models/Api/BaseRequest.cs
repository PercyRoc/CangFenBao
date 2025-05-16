using System.Text.Json.Serialization;

namespace Rookie.Models.Api;

public class BaseRequest
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("requestId")]
    public long RequestId { get; set; }

    [JsonPropertyName("data")]
    public List<CommandData> Data { get; set; } = [];
}

public class CommandData
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public object Params { get; set; } = new();
} 
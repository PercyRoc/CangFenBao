using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rookie.Models.Api;

public class BaseResponse
{
    [JsonPropertyName("requestId")]
    public long RequestId { get; set; }

    [JsonPropertyName("result")]
    public List<CommandResult> Result { get; set; } = [];
}

public class CommandResult
{
    [JsonPropertyName("code")]
    public int Code { get; set; } // 0 = success

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    // Use JsonElement to delay parsing until we know the command type
    [JsonPropertyName("params")]
    public JsonElement Params { get; set; }
}
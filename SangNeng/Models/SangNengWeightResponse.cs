using System.Text.Json.Serialization;

namespace Sunnen.Models;

public class SangNengWeightResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }

    [JsonPropertyName("message")] public string? Message { get; set; }
}
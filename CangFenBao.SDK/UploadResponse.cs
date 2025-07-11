using System.Text.Json.Serialization;

namespace CangFenBao.SDK;

public class UploadResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("chute")]
    public int Chute { get; set; }
}
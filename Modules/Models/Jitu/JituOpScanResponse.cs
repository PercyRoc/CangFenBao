using Newtonsoft.Json;

namespace Modules.Models.Jitu;

public class JituOpScanResponse
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("code")]
    public int Code { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; }
}
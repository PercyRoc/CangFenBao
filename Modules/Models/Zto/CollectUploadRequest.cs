using Newtonsoft.Json;

namespace ShanghaiModuleBelt.Models.Zto;

public class CollectUploadRequest
{
    /// <summary>
    ///     批量上传数据
    /// </summary>
    [JsonProperty("collectUploadDTOS")]
    public List<CollectUploadDTO> CollectUploadDTOS { get; set; } = [];
}
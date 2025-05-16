using System.Collections.Generic;
using System.Text.Json.Serialization; // Ensure this is used
// using System.Xml.Serialization; // No longer needed for request body

namespace SowingWall.Models.WangDianTong.Api
{
    // No [XmlRoot] needed
    public class PickOrderTaskGetRequest
    {
        [JsonPropertyName("owner_no")]
        public string? OwnerNo { get; set; }

        [JsonPropertyName("warehouse_no")]
        public string? WarehouseNo { get; set; }

        [JsonPropertyName("pick_type")]
        public int? PickType { get; set; }

        [JsonPropertyName("picker_short_name")]
        public string PickerShortName { get; set; } = string.Empty; // 必填

        [JsonPropertyName("status")]
        public int? Status { get; set; } // pick_no、status 不能同时为空

        [JsonPropertyName("pick_no")]
        public string? PickNo { get; set; } // pick_no、status 不能同时为空

        [JsonPropertyName("empty_picker_order")]
        public int EmptyPickerOrder { get; set; } // 必填，0不返回，1则返回
    }
} 
using Common.Services.Settings;

namespace XinBa.Models.Settings
{
    [Configuration("XinBa.WildberriesApiSettings")]
    public class WildberriesApiSettings
    {
        public string BaseUrl { get; set; } = "https://wh-skud-external.wildberries.ru";
        public string TareAttributesEndpoint { get; set; } = "/srv/measure_machine_api/api/tare_attributes_from_machine";
        public string Username { get; set; } = "yaoli";
        public string Password { get; set; } = "L4T97kdYBKHg1YTkSmccy3YvnSibr4z66NtpxJ28buSjaXdIKEMJvbY8bqewbkIi";
    }
} 
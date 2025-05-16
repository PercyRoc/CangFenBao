using Common.Services.Settings; // for ConfigurationAttribute
using System.ComponentModel.DataAnnotations;

namespace SowingWall.Models.Settings
{
    [Configuration("SowingWall_ModbusTcp")] // 配置键名，对应 Settings/SowingWall_ModbusTcp.json
    public class ModbusTcpSettings
    {
        [Required(AllowEmptyStrings = false, ErrorMessage = "PLC IP 地址不能为空")]
        [RegularExpression(@"^((25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$", ErrorMessage = "请输入有效的 IP 地址")]
        public string PlcIpAddress { get; set; } = "192.168.1.10"; // 默认IP

        [Range(1, 65535, ErrorMessage = "端口号必须在 1 到 65535 之间")]
        public int PlcPort { get; set; } = 502; // Modbus TCP 默认端口

        [Range(0, 255, ErrorMessage = "从站 ID 必须在 0 到 255 之间")]
        public byte SlaveId { get; set; } = 1; // 默认从站 ID
    }
} 
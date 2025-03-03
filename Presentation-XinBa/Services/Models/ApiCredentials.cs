using System.ComponentModel.DataAnnotations;
using CommonLibrary.Models.Settings;

namespace Presentation_XinBa.Services.Models;

/// <summary>
/// API凭证配置
/// </summary>
[Configuration("ApiCredentials")]
public class ApiCredentials
{
    /// <summary>
    /// API用户名
    /// </summary>
    [Required]
    public string Username { get; set; } = string.Empty;
    
    /// <summary>
    /// API密码
    /// </summary>
    [Required]
    public string Password { get; set; } = string.Empty;
    
    /// <summary>
    /// API基础URL
    /// </summary>
    [Required]
    public string BaseUrl { get; set; } = "https://sorter-ai.wb.ru/dimensions-proxy";
} 
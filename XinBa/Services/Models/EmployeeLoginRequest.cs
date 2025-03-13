using System.ComponentModel.DataAnnotations;

namespace Presentation_XinBa.Services.Models;

/// <summary>
///     员工登录请求
/// </summary>
public class EmployeeLoginRequest
{
    /// <summary>
    ///     员工ID
    /// </summary>
    [Required]
    public int Eid { get; set; }
}
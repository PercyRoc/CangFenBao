using Common.Services.Settings;

namespace XinBa.Services.Models;

/// <summary>
///     员工设置
/// </summary>
[Configuration("Employee")]
public class EmployeeSettings
{
    /// <summary>
    ///     员工ID
    /// </summary>
    public int EmployeeId { get; set; }

    /// <summary>
    ///     是否已登录
    /// </summary>
    public bool IsLoggedIn { get; set; }

    /// <summary>
    ///     登录时间
    /// </summary>
    public DateTime? LoginTime { get; set; }
}
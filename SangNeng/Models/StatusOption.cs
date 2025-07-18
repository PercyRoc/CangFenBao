using Common.Models.Package;

namespace Sunnen.Models;

/// <summary>
///     状态选项
/// </summary>
public class StatusOption
{
    /// <summary>
    ///     构造函数
    /// </summary>
    internal StatusOption(PackageStatus? status, string displayName)
    {
        Status = status;
        DisplayName = displayName;
    }

    /// <summary>
    ///     状态值
    /// </summary>
    internal PackageStatus? Status { get; }

    /// <summary>
    ///     显示名称
    /// </summary>
    public string DisplayName { get; }
}
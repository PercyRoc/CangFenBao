using CommonLibrary.Models;

namespace CangFenBao_SangNeng.Models;

/// <summary>
/// 状态选项
/// </summary>
public class StatusOption
{
    /// <summary>
    /// 状态值
    /// </summary>
    public PackageStatus? Status { get; }
    
    /// <summary>
    /// 显示名称
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// 构造函数
    /// </summary>
    public StatusOption(PackageStatus? status, string displayName)
    {
        Status = status;
        DisplayName = displayName;
    }
} 
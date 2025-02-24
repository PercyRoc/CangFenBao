using System.ComponentModel;
using CommonLibrary.Models.Settings;

namespace Presentation_KuaiLv.Models.Settings.Upload;

/// <summary>
/// 上传环境
/// </summary>
public enum UploadEnvironment
{
    /// <summary>
    /// 测试环境
    /// </summary>
    [Description("测试环境")]
    Test,
    
    /// <summary>
    /// 正式环境
    /// </summary>
    [Description("正式环境")]
    Production
}

/// <summary>
/// 上传配置
/// </summary>
[Configuration("UploadSettings")]
public class UploadConfiguration
{
    /// <summary>
    /// 环境
    /// </summary>
    public UploadEnvironment Environment { get; set; } = UploadEnvironment.Test;
    
    /// <summary>
    /// Secret
    /// </summary>
    public string Secret { get; set; } = "hCcOCuKNTMeikXH3hn1fj7MPV66VUfTk";
}
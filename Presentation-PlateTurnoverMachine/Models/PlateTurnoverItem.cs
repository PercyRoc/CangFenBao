namespace Presentation_PlateTurnoverMachine.Models;

/// <summary>
/// 翻板机配置项
/// </summary>
public class PlateTurnoverItem
{
    /// <summary>
    /// 序号
    /// </summary>
    public string Index { get; set; } = string.Empty;
    
    /// <summary>
    /// 映射格口
    /// </summary>
    public int MappingChute { get; set; }
    
    /// <summary>
    /// TCP地址
    /// </summary>
    public string? TcpAddress { get; set; }
    
    /// <summary>
    /// IO点
    /// </summary>
    public string? IoPoint { get; set; }
    
    /// <summary>
    /// 距离（用于计算光电触发次数）
    /// </summary>
    public double Distance { get; set; }
    
    /// <summary>
    /// 延迟系数（0-1之间）
    /// </summary>
    public double DelayFactor { get; set; } = 0.5;
    
    /// <summary>
    /// 磁铁吸合时间（毫秒）
    /// </summary>
    public int MagnetTime { get; set; } = 200;
}

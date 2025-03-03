namespace Presentation_KuaiLv.Services.Warning;

/// <summary>
///     警示灯服务接口
/// </summary>
public interface IWarningLightService
{
    /// <summary>
    ///     是否已连接
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    ///     显示绿灯
    /// </summary>
    Task ShowGreenLightAsync();

    /// <summary>
    ///     显示红灯
    /// </summary>
    Task ShowRedLightAsync();
    
    /// <summary>
    ///     关闭绿灯
    /// </summary>
    Task TurnOffGreenLightAsync();
    
    /// <summary>
    ///     关闭红灯
    /// </summary>
    Task TurnOffRedLightAsync();

    /// <summary>
    ///     连接警示灯
    /// </summary>
    Task ConnectAsync();

    /// <summary>
    ///     断开连接
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    ///     连接状态变化事件
    /// </summary>
    event Action<bool> ConnectionChanged;
}
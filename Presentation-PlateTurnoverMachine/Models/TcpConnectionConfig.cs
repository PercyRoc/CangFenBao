using System.Net;

namespace Presentation_PlateTurnoverMachine.Models;

/// <summary>
/// TCP连接配置
/// </summary>
public class TcpConnectionConfig
{
    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="ipAddress">IP地址</param>
    /// <param name="port">端口号</param>
    public TcpConnectionConfig(string ipAddress, int port)
    {
        IpAddress = ipAddress;
        Port = port;
    }

    /// <summary>
    /// IP地址
    /// </summary>
    public string IpAddress { get; }
    
    /// <summary>
    /// 端口号
    /// </summary>
    private int Port { get; }
    
    /// <summary>
    /// 获取IPEndPoint
    /// </summary>
    public IPEndPoint GetIpEndPoint() => new(IPAddress.Parse(IpAddress), Port);
    
    /// <summary>
    /// 重写Equals方法，用于比较两个配置是否相同
    /// </summary>
    public override bool Equals(object? obj)
    {
        if (obj is not TcpConnectionConfig other)
            return false;
            
        return IpAddress == other.IpAddress && Port == other.Port;
    }
    
    /// <summary>
    /// 重写GetHashCode方法
    /// </summary>
    public override int GetHashCode()
    {
        return HashCode.Combine(IpAddress, Port);
    }
} 
using System.Text;
using System.Text.RegularExpressions;
using Common.Services.Settings;
using Serilog;
using ShanghaiModuleBelt.Models;
using SortingServices.Common;

namespace Modules.Services;

/// <summary>
///     锁格服务，负责与锁格设备通信
/// </summary>
public partial class LockingService : IDisposable
{
    private readonly object _lockObj = new();
    private bool _disposed;
    private bool _isConnected;
    private TcpClientService? _tcpClient;

    /// <summary>
    ///     初始化锁格服务
    /// </summary>
    /// <param name="settingsService">设置服务</param>
    public LockingService(ISettingsService settingsService)
    {
        var settingsService1 = settingsService;

        // 加载TCP设置
        var settings = settingsService1.LoadSettings<TcpSettings>();

        // 注册设置变更回调
        settingsService1.OnSettingsChanged<TcpSettings>(OnTcpSettingsChanged);

        // 初始化TCP客户端
        InitializeTcpClient(settings.Address, settings.Port);
    }

    /// <summary>
    ///     释放资源
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        DisposeTcpClient();
        _disposed = true;

        GC.SuppressFinalize(this);
    }

    // 锁格状态变更事件
    public event Action<int, bool>? ChuteLockStatusChanged;

    // 连接状态变更事件
    public event Action<bool>? ConnectionStatusChanged;

    /// <summary>
    ///     初始化TCP客户端
    /// </summary>
    /// <param name="address">TCP地址</param>
    /// <param name="port">端口号</param>
    private void InitializeTcpClient(string address, int port)
    {
        try
        {
            // 如果已存在客户端，先释放
            DisposeTcpClient();

            // 创建新的TCP客户端
            _tcpClient = new TcpClientService(
                "锁格设备",
                address,
                port,
                OnDataReceived,
                OnConnectionStatusChanged);

            // 尝试连接
            _tcpClient.Connect();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化锁格设备TCP客户端失败: {Address}:{Port}", address, port);
        }
    }

    /// <summary>
    ///     处理接收到的数据
    /// </summary>
    /// <param name="data">接收到的数据</param>
    private void OnDataReceived(byte[] data)
    {
        try
        {
            // 将字节数组转换为字符串
            var message = Encoding.ASCII.GetString(data);
            Log.Debug("从锁格设备接收到数据: {Message}", message);

            // 使用正则表达式解析锁格状态消息
            // 格式: +OCCH<n>:<sta> 其中n代表格口，sta代表状态(1=锁定，0=解锁)
            var regex = MyRegex();
            var match = regex.Match(message);

            if (match is { Success: true, Groups.Count: >= 3 })
            {
                // 解析格口号和状态
                if (int.TryParse(match.Groups[1].Value, out var chuteNumber) &&
                    int.TryParse(match.Groups[2].Value, out var statusValue))
                {
                    var isLocked = statusValue == 1;

                    // 记录锁格状态变更
                    Log.Information("格口 {ChuteNumber} 状态变更: {Status}",
                        chuteNumber, isLocked ? "锁定" : "解锁");

                    // 触发锁格状态变更事件
                    ChuteLockStatusChanged?.Invoke(chuteNumber, isLocked);
                }
                else
                {
                    Log.Warning("无法解析锁格状态消息中的数字: {Message}", message);
                }
            }
            else
            {
                // 如果不是锁格状态消息，记录原始消息
                Log.Debug("收到非锁格状态消息: {Message}", message);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理锁格设备数据时出错");
        }
    }

    /// <summary>
    ///     处理连接状态变更
    /// </summary>
    /// <param name="isConnected">是否已连接</param>
    private void OnConnectionStatusChanged(bool isConnected)
    {
        lock (_lockObj)
        {
            if (_isConnected == isConnected) return;

            _isConnected = isConnected;
            Log.Information("锁格设备连接状态变更: {Status}", isConnected ? "已连接" : "已断开");

            // 触发连接状态变更事件
            ConnectionStatusChanged?.Invoke(isConnected);
        }
    }

    /// <summary>
    ///     处理TCP设置变更
    /// </summary>
    /// <param name="settings">新的TCP设置</param>
    private void OnTcpSettingsChanged(TcpSettings settings)
    {
        Log.Information("TCP设置已变更，重新初始化锁格设备连接: {Address}:{Port}", settings.Address, settings.Port);
        InitializeTcpClient(settings.Address, settings.Port);
    }

    /// <summary>
    ///     获取连接状态
    /// </summary>
    /// <returns>是否已连接</returns>
    internal bool IsConnected()
    {
        return _isConnected;
    }

    /// <summary>
    ///     释放TCP客户端
    /// </summary>
    private void DisposeTcpClient()
    {
        if (_tcpClient == null) return;

        try
        {
            _tcpClient.Dispose();
            _tcpClient = null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放锁格设备TCP客户端时出错");
        }
    }

    [GeneratedRegex(@"\+OCCH(\d+):(\d+)")]
    private static partial Regex MyRegex();
}
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Threading;
using SeedingWall.Models;
using Serilog;

namespace SeedingWall.Services;

/// <summary>
///     PLC通信服务实现
/// </summary>
internal class PlcService : IPlcService
{
    private readonly byte[] _buffer = new byte[1024];
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _cancellationTokenSource;
    private int _currentPackageNumber = 1;
    private int _currentSequenceNumber = 1;
    private bool _disposed;
    private NetworkStream? _networkStream;
    private TcpClient? _tcpClient;
    private TcpListener? _tcpListener;

    /// <summary>
    ///     构造函数
    /// </summary>
    public PlcService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>
    ///     连接状态变更事件
    /// </summary>
    public event Action<bool>? ConnectionStatusChanged;

    /// <summary>
    ///     收到PLC指令事件
    /// </summary>
    public event Action<PlcCommand>? CommandReceived;

    /// <summary>
    ///     错误事件
    /// </summary>
    public event Action<Exception>? ErrorOccurred;

    /// <summary>
    ///     是否已连接
    /// </summary>
    public bool IsConnected => _tcpClient is { Connected: true };

    /// <summary>
    ///     启动TCP服务器
    /// </summary>
    /// <param name="ip">服务器IP地址</param>
    /// <param name="port">服务器端口</param>
    /// <returns>启动结果</returns>
    public async Task<bool> StartServerAsync(string ip, int port)
    {
        try
        {
            // 如果已经启动，先停止
            StopServer();

            Log.Information("正在启动PLC TCP服务器: {Ip}:{Port}", ip, port);

            // 创建TCP监听器
            var ipAddress = IPAddress.Parse(ip);
            _tcpListener = new TcpListener(ipAddress, port);
            _tcpListener.Start();

            // 创建取消令牌
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            // 异步接受客户端连接
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        Log.Information("等待PLC客户端连接...");

                        // 等待客户端连接
                        _tcpClient = await _tcpListener.AcceptTcpClientAsync(token);

                        Log.Information("PLC客户端已连接: {RemoteEndPoint}", _tcpClient.Client.RemoteEndPoint);

                        // 通知连接状态变更
                        await _dispatcher.BeginInvoke(() => ConnectionStatusChanged?.Invoke(true));

                        // 获取网络流
                        _networkStream = _tcpClient.GetStream();

                        // 开始接收数据
                        await ReceiveDataAsync(token);

                        // 如果连接断开，等待新的连接
                        if (_tcpClient != null)
                        {
                            _tcpClient.Close();
                            _tcpClient = null;
                        }

                        // 通知连接状态变更
                        await _dispatcher.BeginInvoke(() => ConnectionStatusChanged?.Invoke(false));
                    }
                }
                catch (OperationCanceledException)
                {
                    Log.Information("PLC TCP服务器已取消");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "PLC TCP服务器发生错误");
                    await _dispatcher.BeginInvoke(() => ErrorOccurred?.Invoke(ex));
                }
            }, token);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动PLC TCP服务器时发生错误");
            await _dispatcher.BeginInvoke(() => ErrorOccurred?.Invoke(ex));
            return false;
        }
    }

    /// <summary>
    ///     停止TCP服务器
    /// </summary>
    public void StopServer()
    {
        try
        {
            // 取消异步操作
            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
            }

            // 关闭网络流
            if (_networkStream != null)
            {
                _networkStream.Close();
                _networkStream = null;
            }

            // 关闭客户端连接
            if (_tcpClient != null)
            {
                _tcpClient.Close();
                _tcpClient = null;
            }

            // 停止TCP监听器
            if (_tcpListener != null)
            {
                _tcpListener.Stop();
                _tcpListener = null;
            }

            Log.Information("PLC TCP服务器已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止PLC TCP服务器时发生错误");
        }
    }

    /// <summary>
    ///     发送分拣指令
    /// </summary>
    /// <param name="packageNumber">包裹号</param>
    /// <param name="slotNumber">格口号</param>
    /// <returns>发送结果</returns>
    public async Task<bool> SendSortingCommandAsync(int packageNumber, int slotNumber)
    {
        try
        {
            if (_tcpClient is not { Connected: true } || _networkStream == null)
            {
                Log.Warning("无法发送分拣指令，PLC未连接");
                return false;
            }

            // 创建分拣指令
            var command = new PlcCommand
            {
                CommandType = PlcCommandType.SortingCommand,
                PackageNumber = packageNumber > 0 ? packageNumber : GetNextPackageNumber(),
                SlotNumber = slotNumber,
                SequenceNumber = GetNextSequenceNumber()
            };

            // 转换为字符串
            var commandString = command.ToString();
            Log.Debug("发送分拣指令: {Command}", commandString);

            // 转换为字节数组
            var data = Encoding.ASCII.GetBytes(commandString);

            // 发送数据
            await _networkStream.WriteAsync(data);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送分拣指令时发生错误");
            await _dispatcher.BeginInvoke(() => ErrorOccurred?.Invoke(ex));
            return false;
        }
    }

    /// <summary>
    ///     释放资源
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     接收数据
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    private async Task ReceiveDataAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (_tcpClient is { Connected: true } && !cancellationToken.IsCancellationRequested)
            {
                // 清空缓冲区
                Array.Clear(_buffer, 0, _buffer.Length);

                // 读取数据
                var bytesRead = await _networkStream!.ReadAsync(_buffer, cancellationToken);

                if (bytesRead > 0)
                {
                    // 转换为字符串
                    var data = Encoding.ASCII.GetString(_buffer, 0, bytesRead);
                    Log.Debug("收到PLC数据: {Data}", data);

                    // 解析指令
                    try
                    {
                        var command = PlcCommand.Parse(data);

                        // 通知收到指令
                        await _dispatcher.BeginInvoke(() => CommandReceived?.Invoke(command));
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "解析PLC指令时发生错误: {Data}", data);
                    }
                }
                else
                {
                    // 连接已关闭
                    Log.Information("PLC客户端已断开连接");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("PLC数据接收已取消");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "接收PLC数据时发生错误");
            await _dispatcher.BeginInvoke(() => ErrorOccurred?.Invoke(ex));
        }
    }

    /// <summary>
    ///     获取下一个包裹号（1-100循环）
    /// </summary>
    /// <returns>包裹号</returns>
    private int GetNextPackageNumber()
    {
        var packageNumber = _currentPackageNumber;
        _currentPackageNumber = _currentPackageNumber % 100 + 1;
        return packageNumber;
    }

    /// <summary>
    ///     获取下一个报文序号（1-9循环）
    /// </summary>
    /// <returns>报文序号</returns>
    private int GetNextSequenceNumber()
    {
        var sequenceNumber = _currentSequenceNumber;
        _currentSequenceNumber = _currentSequenceNumber % 9 + 1;
        return sequenceNumber;
    }

    /// <summary>
    ///     释放资源
    /// </summary>
    /// <param name="disposing">是否正在释放托管资源</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing) StopServer();

        _disposed = true;
    }
}
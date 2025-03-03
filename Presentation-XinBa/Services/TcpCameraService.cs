using System.Net.Sockets;
using System.Text;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using CommonLibrary.Models;
using CommonLibrary.Services;
using Presentation_XinBa.Services.Models;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.IO;

namespace Presentation_XinBa.Services;

/// <summary>
/// TCP相机服务，用于连接相机服务器并接收数据
/// </summary>
public class TcpCameraService : IDisposable
{
    private readonly Subject<PackageInfo> _packageSubject = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly ISettingsService _settingsService;
    
    private TcpClient? _tcpClient;
    private Task? _connectionTask;
    private bool _disposed;
    private CameraConnectionSettings _settings;
    
    /// <summary>
    /// 构造函数
    /// </summary>
    public TcpCameraService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        _settings = LoadSettings();
        IsConnected = false;
    }
    
    /// <summary>
    /// 相机是否已连接
    /// </summary>
    public bool IsConnected { get; private set; }
    
    /// <summary>
    /// 包裹信息流
    /// </summary>
    public IObservable<PackageInfo> PackageStream => _packageSubject.AsObservable();
    
    /// <summary>
    /// 相机连接状态变化事件
    /// </summary>
    public event Action<bool>? ConnectionChanged;
    
    /// <summary>
    /// 启动相机服务
    /// </summary>
    /// <returns>是否成功</returns>
    public bool Start()
    {
        try
        {
            Log.Information("正在启动TCP相机客户端服务...");
            
            // 重新加载配置
            _settings = LoadSettings();
            
            // 启动连接任务
            _connectionTask = Task.Run(ConnectAndProcessDataAsync, _cancellationTokenSource.Token);
            
            Log.Information("TCP相机客户端服务已启动，正在尝试连接到服务器 {ServerIp}:{ServerPort}", 
                _settings.ServerIp, _settings.ServerPort);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动TCP相机客户端服务失败");
            IsConnected = false;
            return false;
        }
    }
    
    /// <summary>
    /// 异步启动相机服务
    /// </summary>
    public async Task<bool> StartAsync()
    {
        return await Task.Run(Start);
    }
    
    /// <summary>
    /// 异步停止相机服务
    /// </summary>
    /// <param name="timeoutMs">超时时间（毫秒）</param>
    /// <returns>操作是否成功</returns>
    public async Task<bool> StopAsync(int timeoutMs = 3000)
    {
        if (!IsConnected && _tcpClient == null) return true;
        
        try
        {
            Log.Information("正在停止TCP相机客户端服务...");
            
            // 取消所有任务
            _cancellationTokenSource.Cancel();
            
            // 关闭TCP客户端
            if (_tcpClient != null)
            {
                try
                {
                    _tcpClient.Close();
                    _tcpClient.Dispose();
                    _tcpClient = null;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "关闭TCP客户端连接时出错");
                }
            }
            
            // 等待连接任务完成
            if (_connectionTask != null)
            {
                var timeoutTask = Task.Delay(timeoutMs);
                var completedTask = await Task.WhenAny(_connectionTask, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    Log.Warning("TCP相机客户端服务超时");
                }
            }
            
            IsConnected = false;
            
            // 添加调试日志，确认事件触发前的状态
            Log.Debug("准备触发ConnectionChanged事件: isConnected=false, 订阅者数量={SubscriberCount}", 
                ConnectionChanged?.GetInvocationList().Length ?? 0);
            
            ConnectionChanged?.Invoke(false);
            
            Log.Information("TCP相机客户端服务已停止");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止TCP相机客户端服务时出错");
            IsConnected = false;
            return false;
        }
    }
    
    /// <summary>
    /// 设置服务器地址和端口
    /// </summary>
    /// <param name="serverIp">服务器IP地址</param>
    /// <param name="serverPort">服务器端口</param>
    public void SetServerAddress(string? serverIp, int serverPort)
    {
        _settings.ServerIp = serverIp;
        _settings.ServerPort = serverPort;
        
        // 保存配置
        SaveSettings();
        
        Log.Information("相机服务器地址已设置为: {ServerIp}:{ServerPort}", serverIp, serverPort);
    }
    
    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        
        StopAsync().GetAwaiter().GetResult();
        
        _packageSubject.Dispose();
        _connectionLock.Dispose();
        _cancellationTokenSource.Dispose();
        
        _disposed = true;
        GC.SuppressFinalize(this);
    }
    
    /// <summary>
    /// 连接到服务器并处理数据
    /// </summary>
    private async Task ConnectAndProcessDataAsync()
    {
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                // 创建新的TCP客户端
                _tcpClient = new TcpClient();
                
                Log.Information("正在连接到相机服务器 {ServerIp}:{ServerPort}...", 
                    _settings.ServerIp, _settings.ServerPort);
                
                // 尝试连接到服务器
                var connectTask = _tcpClient.ConnectAsync(_settings.ServerIp, _settings.ServerPort);
                var timeoutTask = Task.Delay(_settings.ConnectionTimeoutMs);
                
                var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    Log.Warning("连接到相机服务器超时");
                    await CleanupConnectionAsync();
                    await Task.Delay(_settings.ReconnectIntervalMs, _cancellationTokenSource.Token);
                    continue;
                }
                
                // 检查连接是否成功
                if (!_tcpClient.Connected)
                {
                    Log.Warning("无法连接到相机服务器");
                    await CleanupConnectionAsync();
                    await Task.Delay(_settings.ReconnectIntervalMs, _cancellationTokenSource.Token);
                    continue;
                }
                
                // 连接成功
                Log.Information("成功连接到相机服务器");
                IsConnected = true;
                
                // 添加调试日志，确认事件触发前的状态
                Log.Debug("准备触发ConnectionChanged事件: isConnected=true, 订阅者数量={SubscriberCount}", 
                    ConnectionChanged?.GetInvocationList().Length ?? 0);
                
                ConnectionChanged?.Invoke(true);
                
                // 处理数据
                await ProcessDataFromServerAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "连接到相机服务器时出错");
                await CleanupConnectionAsync();
                await Task.Delay(_settings.ReconnectIntervalMs, _cancellationTokenSource.Token);
            }
        }
    }
    
    /// <summary>
    /// 处理来自服务器的数据
    /// </summary>
    private async Task ProcessDataFromServerAsync()
    {
        try
        {
            if (_tcpClient is not { Connected: true })
                return;

            await using var stream = _tcpClient.GetStream();
            var buffer = new byte[4096];
            var data = new StringBuilder();
            
            while (!_cancellationTokenSource.Token.IsCancellationRequested && _tcpClient.Connected)
            {
                try
                {
                    // 设置读取超时
                    var readTask = stream.ReadAsync(buffer, 0, buffer.Length, _cancellationTokenSource.Token);
                    var timeoutTask = Task.Delay(_settings.ConnectionTimeoutMs, _cancellationTokenSource.Token);
                    
                    var completedTask = await Task.WhenAny(readTask, timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        // 检查连接是否仍然有效
                        if (!IsClientConnected(_tcpClient))
                        {
                            Log.Warning("与相机服务器的连接已断开");
                            break;
                        }
                        
                        continue;
                    }
                    
                    var bytesRead = await readTask;
                    
                    if (bytesRead == 0)
                    {
                        // 服务器已关闭连接
                        Log.Information("相机服务器已关闭连接");
                        break;
                    }
                    
                    // 将接收到的数据添加到缓冲区
                    var receivedData = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    data.Append(receivedData);
                    
                    // 处理完整的数据行
                    ProcessData(data);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理相机服务器数据时出错");
                    await Task.Delay(1000, _cancellationTokenSource.Token);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理相机服务器数据流时出错");
        }
    }
    
    /// <summary>
    /// 处理接收到的数据
    /// </summary>
    /// <param name="data">数据缓冲区</param>
    private void ProcessData(StringBuilder data)
    {
        var content = data.ToString();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        // 清除缓冲区，保留最后一行（可能不完整）
        data.Clear();
        
        if (lines.Length > 0 && !content.EndsWith('\n'))
        {
            data.Append(lines[^1]);
            lines = lines[..^1];
        }
        
        foreach (var line in lines)
        {
            try
            {
                // 解析相机数据
                // 格式: {条码},{重量},{长度},{宽度},{高度},{时间戳}
                var parts = line.Split(',');
                
                if (parts.Length >= 6)
                {
                    var packageInfo = new PackageInfo
                    {
                        Barcode = parts[0].Trim(),
                        CreateTime = DateTime.Now,
                        Status = PackageStatus.Created
                    };
                    
                    // 解析重量
                    if (float.TryParse(parts[1].Trim(), out var weight))
                    {
                        packageInfo.Weight = weight;
                    }
                    
                    // 解析长度
                    if (double.TryParse(parts[2].Trim(), out var length))
                    {
                        packageInfo.Length = length;
                    }
                    
                    // 解析宽度
                    if (double.TryParse(parts[3].Trim(), out var width))
                    {
                        packageInfo.Width = width;
                    }
                    
                    // 解析高度
                    if (double.TryParse(parts[4].Trim(), out var height))
                    {
                        packageInfo.Height = height;
                    }
                    
                    // 解析时间戳
                    if (DateTime.TryParse(parts[5].Trim(), out var timestamp))
                    {
                        packageInfo.TriggerTimestamp = timestamp;
                    }
                    
                    // 查找并加载图像
                    try
                    {
                        var imagePath = FindImageForBarcode(packageInfo.Barcode);
                        if (!string.IsNullOrEmpty(imagePath))
                        {
                            packageInfo.ImagePath = imagePath;
                            Log.Debug("已为包裹 {Barcode} 设置图像路径: {ImagePath}", packageInfo.Barcode, imagePath);
                            
                            // 尝试加载图像
                            try
                            {
                                packageInfo.Image = Image.Load<Rgba32>(imagePath);
                                Log.Debug("已为包裹 {Barcode} 加载图像", packageInfo.Barcode);
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "加载包裹 {Barcode} 的图像失败: {ImagePath}", packageInfo.Barcode, imagePath);
                            }
                        }
                        else
                        {
                            Log.Debug("未找到包裹 {Barcode} 的图像", packageInfo.Barcode);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "查找包裹 {Barcode} 的图像时出错", packageInfo.Barcode);
                    }
                    
                    // 发布包裹信息
                    _packageSubject.OnNext(packageInfo);
                    Log.Information("已接收并处理包裹数据: 条码={Barcode}, 重量={Weight}kg", 
                        packageInfo.Barcode, packageInfo.Weight);
                }
                else
                {
                    Log.Warning("接收到格式不正确的数据: {Line}", line);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理数据行时出错: {Line}", line);
            }
        }
    }
    
    /// <summary>
    /// 查找指定条码的图像文件
    /// </summary>
    /// <param name="barcode">条码</param>
    /// <returns>图像文件路径，如果未找到则返回null</returns>
    private string? FindImageForBarcode(string barcode)
    {
        if (string.IsNullOrEmpty(barcode) || string.IsNullOrEmpty(_settings.ImageSavePath))
            return null;
            
        try
        {
            // 确保图像保存路径存在
            var imagePath = _settings.ImageSavePath;
            if (!Directory.Exists(imagePath))
            {
                Log.Warning("图像保存路径不存在: {ImagePath}", imagePath);
                return null;
            }
            
            // 查找匹配条码的图像文件
            var imageFiles = Directory.GetFiles(imagePath, $"{barcode}*.*")
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || 
                           f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || 
                           f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || 
                           f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).CreationTime)
                .ToArray();
                
            if (imageFiles.Length > 0)
            {
                Log.Debug("找到包裹 {Barcode} 的图像: {ImagePath}", barcode, imageFiles[0]);
                return imageFiles[0];
            }
            
            Log.Debug("未找到包裹 {Barcode} 的图像", barcode);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "查找包裹 {Barcode} 的图像时出错", barcode);
            return null;
        }
    }
    
    /// <summary>
    /// 检查客户端是否仍然连接
    /// </summary>
    /// <param name="client">TCP客户端</param>
    /// <returns>是否连接</returns>
    private static bool IsClientConnected(TcpClient client)
    {
        try
        {
            if (!client.Connected)
                return false;

            if (!client.Client.Poll(0, SelectMode.SelectRead)) return true;
            var buff = new byte[1];
            return client.Client.Receive(buff, SocketFlags.Peek) != 0;

        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// 清理连接资源
    /// </summary>
    private async Task CleanupConnectionAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            if (_tcpClient != null)
            {
                try
                {
                    _tcpClient.Close();
                    _tcpClient.Dispose();
                    _tcpClient = null;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "清理TCP客户端连接时出错");
                }
            }
            
            if (IsConnected)
            {
                IsConnected = false;
                ConnectionChanged?.Invoke(false);
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }
    
    /// <summary>
    /// 加载配置
    /// </summary>
    private CameraConnectionSettings LoadSettings()
    {
        try
        {
            // 尝试加载配置
            var settings = _settingsService.LoadConfiguration<CameraConnectionSettings>();
            Log.Information("已加载相机连接配置");
            return settings;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载相机连接配置失败，使用默认配置");
            return new CameraConnectionSettings();
        }
    }
    
    /// <summary>
    /// 保存配置
    /// </summary>
    private void SaveSettings()
    {
        try
        {
            _settingsService.SaveConfiguration(_settings);
            Log.Information("已保存相机连接配置");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存相机连接配置失败");
        }
    }
} 
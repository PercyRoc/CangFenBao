using System.Globalization;
using System.Text;
using Common.Models.Package;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.TCP;
using Serilog;
using XinBa.Services.Models;

namespace XinBa.Services;

/// <summary>
///     Represents a single volume measurement record with its timestamp.
/// </summary>
public record VolumeRecord(DateTime Timestamp, double Length, double Width, double Height);

/// <summary>
///     连接体积测量设备并接收数据的服务。
/// </summary>
public class VolumeDataService : IDisposable
{
    private const int MaxCacheSize = 500;
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromSeconds(30);
    private readonly object _lock = new();
    private readonly ISettingsService _settingsService;
    private readonly List<VolumeRecord> _volumeCache = [];
    private readonly AutoResetEvent _volumeReceived = new(false); // 1. 添加AutoResetEvent
    private bool _disposed;
    private bool _isConnected;
    private VolumeCameraSettings? _settings;
    private TcpClientService? _tcpClientService;

    public VolumeDataService(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        Log.Information("体积数据服务已创建。");
    }

    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _isConnected;
            }
        }
        private set
        {
            lock (_lock)
            {
                if (_isConnected == value) return;
                _isConnected = value;
            }

            ConnectionChanged?.Invoke(value);
            Log.Information("体积数据服务连接状态已更改为: {Status}", value);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public event Action<bool>? ConnectionChanged;

    private bool Initialize()
    {
        if (_tcpClientService != null) return true;

        try
        {
            _settings = _settingsService.LoadSettings<VolumeCameraSettings>();
            if (_settings == null || string.IsNullOrEmpty(_settings.IpAddress) || _settings.Port == 0)
            {
                Log.Warning("体积相机设置无效或未配置 (IP: {IpAddress}, Port: {Port})。无法初始化TCP客户端。",
                    _settings?.IpAddress ?? "null", _settings?.Port ?? 0);
                return false;
            }

            Log.Information("使用设置初始化体积数据服务: IP={IpAddress}, Port={Port}, 时间窗口=[{MinTime}ms-{MaxTime}ms]", 
                _settings.IpAddress, _settings.Port, _settings.MinFusionTimeMs, _settings.MaxFusionTimeMs);

            _tcpClientService = new TcpClientService(
                "VolumeCamera",
                _settings.IpAddress,
                _settings.Port,
                HandleDataReceived,
                HandleConnectionStatusChanged
            );
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "因加载设置或创建TcpClientService出错，初始化体积数据服务失败。");
            _settings = null;
            _tcpClientService = null;
            return false;
        }
    }

    public void Start()
    {
        if (_disposed)
        {
            Log.Warning("体积数据服务已被释放，无法启动。");
            return;
        }

        if (!Initialize())
        {
            Log.Warning("体积数据服务初始化失败，无法启动连接。");
            return;
        }

        if (_tcpClientService == null)
        {
            Log.Error("无法启动体积数据服务连接：初始化尝试后TcpClientService为空。");
            return;
        }

        Log.Information("尝试启动体积数据服务连接到 {IP}:{Port}...", _settings?.IpAddress, _settings?.Port);
        try
        {
            _tcpClientService.Connect();
            Log.Information("体积数据服务连接请求已发送，等待连接状态回调...");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动体积数据服务连接失败。");
        }
    }

    public void Stop()
    {
        Log.Information("正在停止体积数据服务...");
        Dispose();
    }

    private void HandleConnectionStatusChanged(bool isConnected)
    {
        var previousState = IsConnected;
        IsConnected = isConnected;
        
        Log.Information("体积数据服务连接状态变化: {Previous} -> {Current}, TCP地址: {Address}", 
            previousState, isConnected, $"{_settings?.IpAddress}:{_settings?.Port}");
            
        if (isConnected)
        {
            Log.Information("体积数据服务已成功连接，准备接收数据");
        }
        else
        {
            Log.Warning("体积数据服务连接断开，将无法接收新的体积数据");
        }
    }

    private void HandleDataReceived(byte[] data)
    {
        try
        {
            var rawString = Encoding.UTF8.GetString(data).Trim();
            Log.Information("体积数据服务收到原始数据: {RawData}, 长度: {Length}, 当前时间: {CurrentTime:O}", 
                rawString, data.Length, DateTime.Now);

            var parts = rawString.Split(',');

            if (parts.Length != 3)
            {
                Log.Warning("接收到的体积数据格式无效 (部分数量不为 3): {RawData}, 实际部分数: {PartCount}", 
                    rawString, parts.Length);
                return;
            }

            if (double.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, out var length) &&
                double.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, out var width) &&
                double.TryParse(parts[2].Trim(), CultureInfo.InvariantCulture, out var height))
            {
                var timestamp = DateTime.Now;
                var record = new VolumeRecord(timestamp, length, width, height);
                Log.Information("解析体积数据成功: L={Length}, W={Width}, H={Height}, 时间戳: {Timestamp:O}", 
                    length, width, height, timestamp);

                lock (_lock)
                {
                    var oldCacheCount = _volumeCache.Count;
                    _volumeCache.Add(record);
                    CleanupCache_NoLock();
                    var newCacheCount = _volumeCache.Count;
                    
                    Log.Debug("体积数据已添加到缓存, 缓存数量: {OldCount} -> {NewCount}, 数据: L={L}, W={W}, H={H}", 
                        oldCacheCount, newCacheCount, length, width, height);
                }
                
                _volumeReceived.Set(); // 2. 收到数据后，发出信号
            }
            else
            {
                Log.Warning("解析体积数据失败: {RawData}, 解析结果: L=[{L}], W=[{W}], H=[{H}]", 
                    rawString, parts[0].Trim(), parts[1].Trim(), parts[2].Trim());
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "体积数据服务处理接收数据时出错: {RawData}", Encoding.UTF8.GetString(data));
        }
    }

    public (double Length, double Width, double Height)? FindVolumeData(PackageInfo package)
    {
        if (_settings == null || _disposed)
        {
            Log.Warning("无法查找体积数据：服务未初始化或已释放，或设置未加载。");
            return null;
        }

        var baseTime = package.CreateTime;
        if (baseTime <= DateTime.MinValue + TimeSpan.FromSeconds(1))
        {
            Log.Error("无法查找体积数据 包裹: {Index}, 使用的基准时间无效或过早: {BaseTime:O}", package.Index, baseTime);
            return null;
        }
        
        var lowerBound = baseTime.AddMilliseconds(_settings.MinFusionTimeMs);
        var upperBound = baseTime.AddMilliseconds(_settings.MaxFusionTimeMs);

        Log.Information("查找体积数据 包裹: {Index}, 基准时间: {BaseTime:O}, 时间窗口: [{LowerBound:O} - {UpperBound:O}], 缓存记录数: {CacheCount}",
            package.Index, baseTime, lowerBound, upperBound, _volumeCache.Count);
        
        // 1. 初始查找 (在短暂的锁中完成)
        lock (_lock)
        {
            var initialRecord = _volumeCache
                .Where(r => r.Timestamp >= lowerBound && r.Timestamp <= upperBound)
                .OrderBy(r => Math.Abs((r.Timestamp - baseTime).TotalMilliseconds))
                .FirstOrDefault();

            if (initialRecord != null)
            {
                var timeDiff = (initialRecord.Timestamp - baseTime).TotalMilliseconds;
                Log.Information("初始查找即找到匹配的体积数据: 包裹 {Index}, 时间差 {TimeDiff:F0}ms, L={L}, W={W}, H={H}",
                    package.Index, timeDiff, initialRecord.Length, initialRecord.Width, initialRecord.Height);
                return (initialRecord.Length, initialRecord.Width, initialRecord.Height);
            }
        }

        // 2. 如果找不到，则进入等待阶段 (在锁外部)
        var waitTime = upperBound - DateTime.Now;
        if (waitTime <= TimeSpan.Zero)
        {
            Log.Warning("等待时间已过，未找到包裹 {Index} 的体积数据", package.Index);
            return null;
        }

        Log.Debug("未立即找到体积数据，开始等待，最大等待时间: {WaitTime:F0}ms", waitTime.TotalMilliseconds);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < waitTime && IsConnected)
        {
            var remainingTime = waitTime - sw.Elapsed;
            if (remainingTime <= TimeSpan.Zero) break;

            var currentWaitTimeout = TimeSpan.FromMilliseconds(Math.Min(100, remainingTime.TotalMilliseconds));

            if (_volumeReceived.WaitOne(currentWaitTimeout))
            {
                Log.Debug("收到新的体积数据信号，重新在时间窗口内查找");
                // 收到信号后，短暂加锁检查缓存
                lock (_lock)
                {
                    var recordAfterWait = _volumeCache
                        .Where(r => r.Timestamp >= lowerBound && r.Timestamp <= upperBound)
                        .OrderBy(r => Math.Abs((r.Timestamp - baseTime).TotalMilliseconds))
                        .FirstOrDefault();

                    if (recordAfterWait != null)
                    {
                        sw.Stop();
                        var timeDiff = (recordAfterWait.Timestamp - baseTime).TotalMilliseconds;
                        Log.Information("等待后找到匹配的体积数据: 包裹 {Index}, 时间差 {TimeDiff:F0}ms, L={L}, W={W}, H={H}",
                            package.Index, timeDiff, recordAfterWait.Length, recordAfterWait.Width, recordAfterWait.Height);
                        return (recordAfterWait.Length, recordAfterWait.Width, recordAfterWait.Height);
                    }
                }
                Log.Debug("收到信号，但新数据不符合当前包裹的时间范围，继续等待");
            }
        }
        
        sw.Stop();
        Log.Warning("等待结束 ({Elapsed}ms)，仍未找到包裹 {Index} 在时间窗口内的体积数据", sw.ElapsedMilliseconds, package.Index);
        return null;
    }

    private void CleanupCache_NoLock()
    {
        var cutoff = DateTime.Now - CacheExpiry;
        var removedCount = _volumeCache.RemoveAll(r => r.Timestamp < cutoff);
        if (removedCount > 0)
        {
            Log.Debug("清理了 {Count} 条过期的体积缓存记录。", removedCount);
        }

        if (_volumeCache.Count <= MaxCacheSize) return;
        var overflow = _volumeCache.Count - MaxCacheSize;
        _volumeCache.RemoveRange(0, overflow);
        Log.Debug("体积缓存超过最大尺寸，清理了 {Count} 条最旧的记录。", overflow);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            Log.Information("正在释放体积数据服务...");
            lock (_lock)
            {
                _tcpClientService?.Dispose();
                _tcpClientService = null;
                _volumeCache.Clear();
            }
            _volumeReceived.Dispose(); // 4. 释放AutoResetEvent
            Log.Information("体积数据服务已释放。");
        }

        _disposed = true;
    }
}
using System.Text;
using Common.Services.Settings;
using Serilog;
using XinBa.Services.Models;
using System.Globalization;
using Common.Models.Package;
using Common.Services.TCP;

namespace XinBa.Services;

/// <summary>
/// Represents a single volume measurement record with its timestamp.
/// </summary>
public record VolumeRecord(DateTime Timestamp, double Length, double Width, double Height);

/// <summary>
/// 连接体积测量设备并接收数据的服务。
/// </summary>
public class VolumeDataService : IDisposable
{
    private readonly ISettingsService _settingsService;
    private TcpClientService? _tcpClientService;
    private VolumeCameraSettings? _settings;
    private bool _disposed;
    private bool _isConnected;
    private readonly object _lock = new();
    private readonly List<VolumeRecord> _volumeCache = [];
    private const int MaxCacheSize = 500;
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromSeconds(30);

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

    public event Action<bool>? ConnectionChanged;

    public VolumeDataService(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        Log.Information("体积数据服务已创建。");
    }

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

            Log.Information("使用设置初始化体积数据服务: IP={IpAddress}, Port={Port}", _settings.IpAddress, _settings.Port);

            _tcpClientService = new TcpClientService(
                deviceName: "VolumeCamera",
                ipAddress: _settings.IpAddress,
                port: _settings.Port,
                dataReceivedCallback: HandleDataReceived,
                connectionStatusCallback: HandleConnectionStatusChanged,
                autoReconnect: true
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

        Log.Information("尝试启动体积数据服务连接...");
        try
        {
            _tcpClientService.Connect();
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
        IsConnected = isConnected;
    }

    private void HandleDataReceived(byte[] data)
    {
        try
        {
            var rawString = Encoding.UTF8.GetString(data).Trim();
            Log.Debug("体积数据服务收到原始数据: {RawData}", rawString);

            var parts = rawString.Split(',');

            if (parts.Length != 3)
            {
                Log.Warning("接收到的体积数据格式无效 (部分数量不为 3): {RawData}", rawString);
                return;
            }

            if (double.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, out var length) &&
                double.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, out var width) &&
                double.TryParse(parts[2].Trim(), CultureInfo.InvariantCulture, out var height))
            {
                var record = new VolumeRecord(DateTime.Now, length, width, height);
                Log.Debug("解析体积数据成功: L={Length}, W={Width}, H={Height}", length, width, height);

                lock (_lock)
                {
                    _volumeCache.Add(record);
                    CleanupCache_NoLock();
                }
            }
            else
            {
                Log.Warning("解析体积数据失败: {RawData}", rawString);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "体积数据服务处理接收数据时出错。");
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

        var maxTimeOffset = TimeSpan.FromMilliseconds(_settings.MaxFusionTimeMs);
        var minTimeOffset = TimeSpan.FromMilliseconds(_settings.MinFusionTimeMs);

        var latestTime = baseTime - minTimeOffset;
        var earliestTime = baseTime - maxTimeOffset;

        Log.Debug("查找体积数据 包裹: {Index}, 基准时间: {BaseTime:O}, 时间窗口: [{Earliest:O} - {Latest:O}]",
            package.Index, baseTime, earliestTime, latestTime);

        VolumeRecord? foundRecord = null;
        lock (_lock)
        {
            for (var i = _volumeCache.Count - 1; i >= 0; i--)
            {
                var record = _volumeCache[i];
                if (record.Timestamp >= earliestTime && record.Timestamp <= latestTime)
                {
                    foundRecord = record;
                    Log.Information("找到匹配的体积数据 包裹: {Index}, 记录时间: {RecordTime:O}, L={L}, W={W}, H={H}",
                        package.Index, record.Timestamp, record.Length, record.Width, record.Height);
                    break;
                }

                if (record.Timestamp < earliestTime)
                {
                    break;
                }
            }
        }

        if (foundRecord != null)
        {
            return (foundRecord.Length, foundRecord.Width, foundRecord.Height);
        }

        Log.Warning("未找到包裹 {Index} 在时间窗口内的体积数据", package.Index);
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

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
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

            Log.Information("体积数据服务已释放。");
        }

        _disposed = true;
    }
}
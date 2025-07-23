using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Common.Services.Settings;
using DeviceService.DataSourceDevices.SerialPort;
using Serilog;

namespace CangFenBao.SDK;

public class SdkWeightService : IAsyncDisposable
{
    private readonly WeightServiceSettings _settings;
    private readonly SerialPortService _serialPortService;
    private readonly StringBuilder _receiveBuffer = new();
    private readonly List<double> _weightSamples = new();
    private const int MaxStableWeightQueueSize = 100;
    public readonly ConcurrentQueue<(double Weight, DateTime Timestamp)> StableWeights = new();
    private double _latestWeightInGrams;

    public double LatestWeightInGrams => _latestWeightInGrams;

    public SdkWeightService(ISettingsService settingsService)
    {
        _settings = settingsService.LoadSettings<WeightServiceSettings>();
        _serialPortService = new SerialPortService("SdkWeightScale", _settings.SerialPortParams);
    }

    public bool IsEnabled => _settings.IsEnabled;

    public Task StartAsync()
    {
        if (!IsEnabled) return Task.CompletedTask;
        Log.Information("正在启动SDK重量服务...");
        _serialPortService.DataReceived += HandleDataReceived;
        if (!_serialPortService.Connect())
        {
            Log.Error("SDK重量服务连接串口失败。");
        }
        return Task.CompletedTask;
    }

    private void HandleDataReceived(byte[] data)
    {
        var receivedString = Encoding.ASCII.GetString(data);
        _receiveBuffer.Append(receivedString);
        ProcessBuffer();
    }

    private void ProcessBuffer()
    {
        var bufferContent = _receiveBuffer.ToString();
        var lastSeparatorIndex = bufferContent.LastIndexOf('=');
        if (lastSeparatorIndex == -1) return;

        var dataToProcess = bufferContent[..lastSeparatorIndex];
        _receiveBuffer.Remove(0, lastSeparatorIndex + 1);

        var segments = dataToProcess.Split(['='], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var valuePart = segment.Length >= 6 ? segment[..6] : segment;
            var reversedValue = new string(valuePart.Reverse().ToArray());

            if (float.TryParse(reversedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var weightInKg))
            {
                var currentWeightG = weightInKg * 1000;
                _latestWeightInGrams = currentWeightG; // 更新最新实时重量
                CheckAndRecordStableWeight(currentWeightG);
            }
        }
    }

    private void CheckAndRecordStableWeight(double currentWeightG)
    {
        _weightSamples.Add(currentWeightG);

        // 维持滑动窗口的大小
        while (_weightSamples.Count > _settings.StableSampleCount)
        {
            _weightSamples.RemoveAt(0);
        }

        // 样本数量不足以判断稳定性
        if (_weightSamples.Count < _settings.StableSampleCount) return;

        // 判断稳定性：最后一个值与窗口内所有其他值的差值绝对值是否都小于阈值
        var lastWeight = _weightSamples[^1];
        var isStable = _weightSamples.Take(_weightSamples.Count - 1)
                                     .All(w => Math.Abs(w - lastWeight) <= _settings.StableThresholdGrams);

        if (isStable)
        {
            var stableTimestamp = DateTime.Now;
            StableWeights.Enqueue((lastWeight, stableTimestamp));
            Log.Debug("重量稳定: {Weight:F2}g，已记录到队列。", lastWeight);

            // 清空样本，等待下一轮稳定判断
            _weightSamples.Clear();

            // 维持稳定队列的大小，防止内存无限增长
            while (StableWeights.Count > MaxStableWeightQueueSize)
            {
                StableWeights.TryDequeue(out _);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsEnabled)
        {
            _serialPortService.DataReceived -= HandleDataReceived;
            _serialPortService.Disconnect();
            _serialPortService.Dispose();
        }
        await Task.CompletedTask;
    }
} 
using System.Collections.Concurrent;
using DeviceService.DataSourceDevices.TCP;
using Serilog;

namespace SortingServices;

/// <summary>
///     硬件命令定义，包含待执行的硬件指令信息
/// </summary>
public class HardwareCommand
{
    /// <summary>
    ///     硬件指令字符串（如 "AT+STACH2=0"）
    /// </summary>
    public string CommandString { get; set; } = string.Empty;

    /// <summary>
    ///     目标设备的TCP客户端
    /// </summary>
    public TcpClientService? TargetDevice { get; set; }

    /// <summary>
    ///     期望的执行时间
    /// </summary>
    public DateTime ExecutionTime { get; set; }

    /// <summary>
    ///     设备名称（用于日志）
    /// </summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    ///     命令类型描述（用于日志和调试）
    /// </summary>
    public string CommandType { get; set; } = string.Empty;

    /// <summary>
    ///     重试回调函数，如果命令发送失败，可以调用此函数进行重试逻辑
    /// </summary>
    public Func<TcpClientService?, byte[], string, bool>? RetryCallback { get; set; }

    /// <summary>
    ///     命令字节数组（预转换好的）
    /// </summary>
    public byte[]? CommandBytes { get; set; }
}

/// <summary>
///     专用硬件命令调度器
///     解决高负载下线程饥饿导致的硬件指令延迟问题
/// </summary>
public static class HardwareCommandScheduler
{
    private static readonly BlockingCollection<HardwareCommand> _commandQueue = new();
    private static readonly Thread _workerThread;
    private static volatile bool _isRunning;
    private static readonly object _lockObject = new();

    static HardwareCommandScheduler()
    {
        _workerThread = new Thread(ProcessCommandQueue)
        {
            Name = "HardwareCommandScheduler-Worker",
            IsBackground = true
        };
    }

    /// <summary>
    ///     启动硬件命令调度器
    /// </summary>
    public static void Start()
    {
        lock (_lockObject)
        {
            if (_isRunning) return;

            _isRunning = true;
            _workerThread.Start();
            Log.Information("硬件命令调度器已启动，专用线程: {ThreadName}", _workerThread.Name);
        }
    }

    /// <summary>
    ///     停止硬件命令调度器
    /// </summary>
    public static void Stop()
    {
        lock (_lockObject)
        {
            if (!_isRunning) return;

            _isRunning = false;
            _commandQueue.CompleteAdding();

            if (_workerThread.IsAlive)
                if (!_workerThread.Join(TimeSpan.FromSeconds(5)))
                    Log.Warning("硬件命令调度器工作线程未能在5秒内正常停止");

            Log.Information("硬件命令调度器已停止");
        }
    }

    /// <summary>
    ///     调度一个硬件命令在指定时间执行
    /// </summary>
    /// <param name="command">要执行的硬件命令</param>
    public static void Schedule(HardwareCommand command)
    {
        if (!_isRunning)
        {
            Log.Warning("硬件命令调度器未启动，无法调度命令: {CommandType} - {CommandString}",
                command.CommandType, command.CommandString);
            return;
        }

        try
        {
            _commandQueue.Add(command);
            Statistics.IncrementScheduled();

            var delayMs = (command.ExecutionTime - DateTime.UtcNow).TotalMilliseconds;
            Log.Debug("硬件命令已调度: {CommandType} - {CommandString}, 设备: {DeviceName}, 延迟: {DelayMs:F1}ms",
                command.CommandType, command.CommandString, command.DeviceName, delayMs);
        }
        catch (InvalidOperationException)
        {
            Log.Warning("硬件命令调度器队列已关闭，无法调度命令");
        }
    }

    /// <summary>
    ///     创建延迟回正命令的便捷方法
    /// </summary>
    public static void ScheduleDelayedReset(TcpClientService? client, string resetCommand, byte[] commandBytes,
        string deviceName, int delayMs, Func<TcpClientService?, byte[], string, bool>? retryCallback = null)
    {
        var command = new HardwareCommand
        {
            CommandString = resetCommand,
            CommandBytes = commandBytes,
            TargetDevice = client,
            DeviceName = deviceName,
            CommandType = "延迟回正",
            ExecutionTime = DateTime.UtcNow.AddMilliseconds(delayMs),
            RetryCallback = retryCallback
        };

        Schedule(command);
    }

    /// <summary>
    ///     专用线程的命令处理循环
    /// </summary>
    private static void ProcessCommandQueue()
    {
        Log.Information("硬件命令调度器工作线程已启动");

        try
        {
            foreach (var command in _commandQueue.GetConsumingEnumerable()) ProcessSingleCommand(command);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "硬件命令调度器工作线程发生异常");
        }

        Log.Information("硬件命令调度器工作线程已停止");
    }

    /// <summary>
    ///     处理单个硬件命令
    /// </summary>
    private static void ProcessSingleCommand(HardwareCommand command)
    {
        try
        {
            // 计算需要等待的时间
            var waitTime = command.ExecutionTime - DateTime.UtcNow;

            if (waitTime > TimeSpan.Zero)
                // 使用Thread.Sleep进行精确等待，因为这是专用线程
                Thread.Sleep(waitTime);

            // 记录实际延迟
            var actualDelay = (DateTime.UtcNow - command.ExecutionTime).TotalMilliseconds;
            Statistics.UpdateMaxDelay((long)Math.Abs(actualDelay));

            if (Math.Abs(actualDelay) > 50) // 如果延迟超过50ms则记录警告
                Log.Warning("硬件命令执行时间偏差: {ActualDelay:F1}ms, 命令: {CommandType} - {CommandString}",
                    actualDelay, command.CommandType, command.CommandString);

            // 执行硬件命令
            var success = ExecuteHardwareCommand(command);

            if (success)
            {
                Statistics.IncrementExecuted();
                Log.Debug("硬件命令执行成功: {CommandType} - {CommandString}, 设备: {DeviceName}",
                    command.CommandType, command.CommandString, command.DeviceName);
            }
            else
            {
                Statistics.IncrementFailed();
                Log.Error("硬件命令执行失败: {CommandType} - {CommandString}, 设备: {DeviceName}",
                    command.CommandType, command.CommandString, command.DeviceName);
            }
        }
        catch (Exception ex)
        {
            Statistics.IncrementFailed();
            Log.Error(ex, "处理硬件命令时发生异常: {CommandType} - {CommandString}",
                command.CommandType, command.CommandString);
        }
    }

    /// <summary>
    ///     执行具体的硬件命令
    /// </summary>
    private static bool ExecuteHardwareCommand(HardwareCommand command)
    {
        if (command.TargetDevice == null)
        {
            Log.Warning("硬件命令目标设备为空: {CommandType}", command.CommandType);
            return false;
        }

        if (!command.TargetDevice.IsConnected())
        {
            Log.Warning("硬件命令目标设备未连接: {DeviceName}", command.DeviceName);
            return false;
        }

        if (command.CommandBytes == null || command.CommandBytes.Length == 0)
        {
            Log.Warning("硬件命令字节数组为空: {CommandType}", command.CommandType);
            return false;
        }

        try
        {
            // 使用重试回调（如果提供）或直接发送
            if (command.RetryCallback != null)
                return command.RetryCallback(command.TargetDevice, command.CommandBytes, command.DeviceName);

            // 默认发送逻辑
            command.TargetDevice.Send(command.CommandBytes);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送硬件命令时发生异常: {CommandType} - {CommandString}",
                command.CommandType, command.CommandString);
            return false;
        }
    }

    /// <summary>
    ///     获取调度器状态信息
    /// </summary>
    public static string GetStatusInfo()
    {
        return $"运行状态: {(_isRunning ? "运行中" : "已停止")}, " +
               $"队列长度: {_commandQueue.Count}, " +
               $"已调度: {Statistics.TotalScheduled}, " +
               $"已执行: {Statistics.TotalExecuted}, " +
               $"失败: {Statistics.TotalFailed}, " +
               $"最大延迟: {Statistics.MaxDelayMs}ms";
    }

    /// <summary>
    ///     调度器统计信息
    /// </summary>
    public static class Statistics
    {
        private static long _totalScheduled;
        private static long _totalExecuted;
        private static long _totalFailed;
        private static long _maxDelayMs;

        public static long TotalScheduled => _totalScheduled;
        public static long TotalExecuted => _totalExecuted;
        public static long TotalFailed => _totalFailed;
        public static long MaxDelayMs => _maxDelayMs;

        internal static void IncrementScheduled()
        {
            Interlocked.Increment(ref _totalScheduled);
        }

        internal static void IncrementExecuted()
        {
            Interlocked.Increment(ref _totalExecuted);
        }

        internal static void IncrementFailed()
        {
            Interlocked.Increment(ref _totalFailed);
        }

        internal static void UpdateMaxDelay(long delayMs)
        {
            var currentMax = _maxDelayMs;
            while (delayMs > currentMax)
            {
                var original = Interlocked.CompareExchange(ref _maxDelayMs, delayMs, currentMax);
                if (original == currentMax) break;
                currentMax = original;
            }
        }

        public static void Reset()
        {
            Interlocked.Exchange(ref _totalScheduled, 0);
            Interlocked.Exchange(ref _totalExecuted, 0);
            Interlocked.Exchange(ref _totalFailed, 0);
            Interlocked.Exchange(ref _maxDelayMs, 0);
        }
    }
}
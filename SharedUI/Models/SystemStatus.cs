using System.Diagnostics;
using System.IO;

namespace SharedUI.Models;

public class DiskStatus
{
    public string Name { get; init; } = string.Empty;
    public double UsagePercentage { get; internal set; }
    public bool IsReady { get; set; }
}

public class SystemStatus
{
    private static readonly PerformanceCounter? CpuCounter;
    private static readonly PerformanceCounter? MemCounter;
    private static readonly DateTime StartTime = DateTime.Now;
    private static DateTime _lastCpuCheck = DateTime.MinValue;
    private static double _lastCpuValue;

    static SystemStatus()
    {
        try
        {
            CpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            MemCounter = new PerformanceCounter("Memory", "% Committed Bytes In Use");
            
            // 初始化CPU计数器 - 第一次调用通常返回0
            _ = CpuCounter.NextValue();
        }
        catch (Exception)
        {
            // 如果性能计数器不可用，设为null，后续使用备用方案
            CpuCounter = null;
            MemCounter = null;
        }
    }

    public double CpuUsage { get; private set; }
    public double MemoryUsage { get; private set; }
    public List<DiskStatus> Disks { get; } = [];

    public TimeSpan RunningTime { get; private set; }

    public static SystemStatus GetCurrentStatus()
    {
        var status = new SystemStatus
        {
            RunningTime = DateTime.Now - StartTime
        };

        // 获取CPU使用率
        try
        {
            if (CpuCounter != null)
            {
                var cpuValue = CpuCounter.NextValue();
                
                // 避免第一次调用返回0的问题
                if (cpuValue > 0 || (DateTime.Now - _lastCpuCheck).TotalSeconds > 1)
                {
                    _lastCpuValue = cpuValue;
                    _lastCpuCheck = DateTime.Now;
                }
                
                status.CpuUsage = _lastCpuValue;
            }
            else
            {
                // 备用方案：使用Process类获取CPU信息
                status.CpuUsage = GetCpuUsageAlternative();
            }
        }
        catch (Exception)
        {
            status.CpuUsage = GetCpuUsageAlternative();
        }

        // 获取内存使用率
        try
        {
            status.MemoryUsage = MemCounter?.NextValue() ?? GetMemoryUsageAlternative();
        }
        catch (Exception)
        {
            status.MemoryUsage = GetMemoryUsageAlternative();
        }

        // 获取硬盘信息
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                var diskStatus = new DiskStatus
                {
                    Name = drive.Name.TrimEnd('\\'),
                    IsReady = drive.IsReady
                };

                if (drive is { IsReady: true, DriveType: DriveType.Fixed })
                {
                    var totalSize = drive.TotalSize;
                    var freeSpace = drive.AvailableFreeSpace;
                    diskStatus.UsagePercentage = (double)(totalSize - freeSpace) / totalSize * 100;
                }

                status.Disks.Add(diskStatus);
            }
        }
        catch (Exception)
        {
            // 如果获取硬盘信息失败，至少显示一个占位符
            status.Disks.Add(new DiskStatus { Name = "C", UsagePercentage = 0, IsReady = false });
        }

        return status;
    }

    private static double GetCpuUsageAlternative()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            return Math.Min(100, process.TotalProcessorTime.TotalMilliseconds / Environment.ProcessorCount / 10);
        }
        catch
        {
            return 0;
        }
    }

    private static double GetMemoryUsageAlternative()
    {
        try
        {
            Process.GetCurrentProcess();
            var totalMemory = GC.GetTotalMemory(false);
            return Math.Min(100, totalMemory / (1024.0 * 1024.0)); // 简化的内存使用量，以MB为单位显示
        }
        catch
        {
            return 0;
        }
    }
}
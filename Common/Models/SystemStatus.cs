using System.Diagnostics;
using System.IO;

namespace Common.Models;

public class DiskStatus
{
    public string Name { get; init; } = string.Empty;
    public double UsagePercentage { get; internal set; }
    public bool IsReady { get; set; }
}

public class SystemStatus
{
    private static readonly PerformanceCounter CpuCounter;
    private static readonly PerformanceCounter MemCounter;
    private static readonly DateTime StartTime = DateTime.Now;

    static SystemStatus()
    {
        CpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        MemCounter = new PerformanceCounter("Memory", "% Committed Bytes In Use");
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

        try
        {
            // CPU使用率
            status.CpuUsage = CpuCounter.NextValue();

            // 内存使用率
            status.MemoryUsage = MemCounter.NextValue();

            // 获取所有硬盘信息
            foreach (var drive in DriveInfo.GetDrives())
            {
                var diskStatus = new DiskStatus
                {
                    Name = drive.Name.TrimEnd('\\'),
                    IsReady = drive.IsReady
                };

                if (drive.IsReady)
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
            // 如果获取失败，使用默认值
            status.CpuUsage = 0;
            status.MemoryUsage = 0;
            status.Disks.Clear();
        }

        return status;
    }
}
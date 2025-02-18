namespace SortingService.Models;

public class PhotoelectricConfig
{
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public int TimeRangeLower { get; set; }
    public int TimeRangeUpper { get; set; }
    public int SortingDelay { get; set; }
    public int ResetDelay { get; set; }
}
using System;

namespace ShanghaiModuleBelt.Models;

public class RetryRecord
{
    public int Id { get; set; }
    public string Barcode { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public string RequestData { get; set; } = string.Empty;
    public DateTime CreateTime { get; set; }
    public DateTime? RetryTime { get; set; }
    public DateTime? LastRetryTime { get; set; }
    public bool IsRetried { get; set; }
    public int RetryCount { get; set; }
    public string? ErrorMessage { get; set; }
} 
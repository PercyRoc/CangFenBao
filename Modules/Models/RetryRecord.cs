using System.ComponentModel.DataAnnotations;

namespace ShanghaiModuleBelt.Models;

public class RetryRecord
{
    public int Id { get; set; }
    
    [Required]
    [MaxLength(100)]
    public string Barcode { get; set; } = string.Empty;
    
    [Required]
    [MaxLength(50)]
    public string Company { get; set; } = string.Empty;
    
    [Required]
    [MaxLength(4000)]
    public string RequestData { get; set; } = string.Empty;
    
    public DateTime CreateTime { get; set; }
    public DateTime? RetryTime { get; set; }
    public DateTime? LastRetryTime { get; set; }
    public bool IsRetried { get; set; }
    public int RetryCount { get; set; }
    
    [MaxLength(1000)]
    public string? ErrorMessage { get; set; }
}
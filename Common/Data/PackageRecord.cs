using Common.Models.Package;
using System.ComponentModel.DataAnnotations;

namespace Common.Data;

/// <summary>
///     包裹记录
/// </summary>
public class PackageRecord
{
    /// <summary>
    ///     主键ID
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    ///     包裹序号
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    ///     条码
    /// </summary>
    [MaxLength(50)]
    public string Barcode { get; set; } = string.Empty;

    /// <summary>
    ///     分段码
    /// </summary>
    [MaxLength(50)]
    public string? SegmentCode { get; set; }

    /// <summary>
    ///     重量（千克）
    /// </summary>
    public float Weight { get; set; }

    /// <summary>
    ///     格口名称
    /// </summary>
    public int? ChuteName { get; set; }

    /// <summary>
    ///     创建时间
    /// </summary>
    public DateTime CreateTime { get; set; }

    /// <summary>
    ///     附加信息
    /// </summary>
    [MaxLength(500)]
    public string? Information { get; set; }

    /// <summary>
    ///     错误信息
    /// </summary>
    [MaxLength(500)]
    public string? ErrorMessage { get; set; }

    /// <summary>
    ///     长度（毫米）
    /// </summary>
    public double? Length { get; set; }

    /// <summary>
    ///     宽度（毫米）
    /// </summary>
    public double? Width { get; set; }

    /// <summary>
    ///     高度（毫米）
    /// </summary>
    public double? Height { get; set; }

    /// <summary>
    ///     体积（立方毫米）
    /// </summary>
    public double? Volume { get; set; }

    /// <summary>
    ///     处理状态
    /// </summary>
    public PackageStatus Status { get; set; }

    /// <summary>
    ///     图片路径
    /// </summary>
    [MaxLength(255)]
    public string? ImagePath { get; set; }

    /// <summary>
    ///     从包裹信息创建记录
    /// </summary>
    public static PackageRecord FromPackageInfo(PackageInfo info)
    {
        return new PackageRecord
        {
            Index = info.Index,
            Barcode = info.Barcode,
            SegmentCode = info.SegmentCode,
            Weight = info.Weight,
            ChuteName = info.ChuteName,
            CreateTime = info.CreateTime,
            Information = info.Information,
            ErrorMessage = info.ErrorMessage,
            Length = info.Length,
            Width = info.Width,
            Height = info.Height,
            Volume = info.Volume,
            Status = info.Status,
            ImagePath = info.ImagePath
        };
    }
}
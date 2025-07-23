using System.ComponentModel.DataAnnotations;
using Common.Models.Package;

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
    public double Weight { get; set; }

    /// <summary>
    ///     格口号
    /// </summary>
    public int? ChuteNumber { get; set; }

    /// <summary>
    ///     完整格口代码（如 "021-A"）
    /// </summary>
    [MaxLength(50)]
    public string? SortPortCode { get; set; }

    /// <summary>
    ///     创建时间
    /// </summary>
    public DateTime CreateTime { get; set; }

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
    ///     状态显示文本
    /// </summary>
    [MaxLength(500)]
    public string StatusDisplay { get; set; } = string.Empty;

    /// <summary>
    ///     图片路径
    /// </summary>
    [StringLength(255)]
    public string? ImagePath { get; set; }

    /// <summary>
    ///     托盘名称 (Sunnen项目专用)
    /// </summary>
    [StringLength(50)]
    public string? PalletName { get; set; }

    /// <summary>
    ///     托盘重量，单位kg (Sunnen项目专用)
    /// </summary>
    public double? PalletWeight { get; set; }

    /// <summary>
    ///     托盘长度，单位cm (Sunnen项目专用)
    /// </summary>
    public double? PalletLength { get; set; }

    /// <summary>
    ///     托盘宽度，单位cm (Sunnen项目专用)
    /// </summary>
    public double? PalletWidth { get; set; }

    /// <summary>
    ///     托盘高度，单位cm (Sunnen项目专用)
    /// </summary>
    public double? PalletHeight { get; set; }

    /// <summary>
    ///     从包裹信息创建记录
    /// </summary>
    internal static PackageRecord FromPackageInfo(PackageInfo info)
    {
        var record = new PackageRecord
        {
            Index = info.Index,
            Barcode = info.Barcode,
            SegmentCode = info.SegmentCode,
            Weight = info.Weight,
            ChuteNumber = info.ChuteNumber,
            SortPortCode = info.SortPortCode,
            CreateTime = info.CreateTime,
            ErrorMessage = info.ErrorMessage,
            Length = info.Length,
            Width = info.Width,
            Height = info.Height,
            Volume = info.Volume,
            Status = info.Status,
            ImagePath = info.ImagePath,
            StatusDisplay = info.StatusDisplay,
            PalletName = info.PalletName,
            PalletWeight = info.PalletWeight,
            PalletLength = info.PalletLength,
            PalletWidth = info.PalletWidth,
            PalletHeight = info.PalletHeight
        };
        return record;
    }
}
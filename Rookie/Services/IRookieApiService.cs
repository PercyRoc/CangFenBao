using Common.Models.Package;
using Rookie.Models.Api;

namespace Rookie.Services;

public interface IRookieApiService
{
    /// <summary>
    /// 上报包裹实测信息 (sorter.parcel_info_upload)
    /// </summary>
    /// <param name="package">包含测量信息的包裹对象</param>
    /// <returns>True if the upload was acknowledged successfully, false otherwise.</returns>
    Task<bool> UploadParcelInfoAsync(PackageInfo package);

    /// <summary>
    /// 请求分拣目的地 (sorter.dest_request)
    /// </summary>
    /// <param name="barcode">包裹条码</param>
    /// <param name="itemBarcode">商品条码 (可选, 默认为 "NoRead")</param>
    /// <returns>The destination result parameters if successful, otherwise null.</returns>
    Task<DestRequestResultParams?> RequestDestinationAsync(string barcode, string itemBarcode = "NoRead");

    /// <summary>
    /// 上报分拣结果 (sorter.sort_report)
    /// </summary>
    /// <param name="barcode">包裹条码</param>
    /// <param name="chuteCode">实际分拣格口</param>
    /// <param name="success">是否成功分拣到目标格口 (True for status 0, False for status 1)</param>
    /// <param name="errorReason">错误原因 (仅在 success 为 false 时相关)</param>
    /// <returns>True if the report was acknowledged successfully, false otherwise.</returns>
    Task<bool> ReportSortResultAsync(string barcode, string chuteCode, bool success, string? errorReason = null);

    /// <summary>
    /// 上传图片文件，返回图片URL（失败返回null）
    /// </summary>
    /// <param name="filePath">本地图片文件路径</param>
    /// <returns>图片URL或null</returns>
    Task<string?> UploadImageAsync(string filePath);
} 
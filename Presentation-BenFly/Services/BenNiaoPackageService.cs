using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using CommonLibrary.Models;
using CommonLibrary.Services;
using FluentFTP;
using Presentation_BenFly.Models.BenNiao;
using Presentation_BenFly.Models.Upload;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Presentation_BenFly.Services;

/// <summary>
///     笨鸟包裹回传服务
/// </summary>
public class BenNiaoPackageService(
    IHttpClientFactory httpClientFactory,
    ISettingsService settingsService,
    BenNiaoPreReportService preReportService)
    : IDisposable
{
    private const string SettingsKey = "UploadSettings";
    private readonly UploadConfiguration _config = settingsService.LoadSettings<UploadConfiguration>(SettingsKey);
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient("BenNiao");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     处理包裹
    /// </summary>
    public async Task<bool> ProcessPackageAsync(PackageInfo package)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(package.Barcode))
            {
                Log.Warning("包裹条码为空，无法处理");
                return false;
            }

            // 从预报数据服务中获取预报数据
            var preReportData = preReportService.GetPreReportData();
            var preReportItem = preReportData?.FirstOrDefault(x => x.WaybillNum == package.Barcode);

            if (preReportItem != null && !string.IsNullOrWhiteSpace(preReportItem.SegmentCode))
            {
                Log.Information("在预报数据中找到包裹 {Barcode} 的三段码：{SegmentCode}", package.Barcode, preReportItem.SegmentCode);
                package.SegmentCode = preReportItem.SegmentCode;
            }
            else
            {
                // 如果预报数据中没有，则实时查询
                var segmentCode = await GetRealTimeSegmentCodeAsync(package.Barcode);
                if (!string.IsNullOrWhiteSpace(segmentCode))
                {
                    Log.Information("通过实时查询获取到包裹 {Barcode} 的三段码：{SegmentCode}", package.Barcode, segmentCode);
                    package.SegmentCode = segmentCode;
                }
                else
                {
                    Log.Warning("无法获取包裹 {Barcode} 的三段码", package.Barcode);
                    return false;
                }
            }

            // 异步上传包裹数据和图片
            if (package is { Length: not null, Width: not null, Height: not null })
                _ = UploadPackageDataAndImageAsync(package);
            else
                Log.Warning("包裹 {Barcode} 缺少必要的数据（重量或尺寸），跳过上传", package.Barcode);

            Log.Information("包裹 {Barcode} 处理完成", package.Barcode);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理包裹 {Barcode} 时发生错误", package.Barcode);
            return false;
        }
    }

    /// <summary>
    ///     实时查询三段码
    /// </summary>
    private async Task<string?> GetRealTimeSegmentCodeAsync(string waybillNum)
    {
        try
        {
            Log.Information("开始实时查询运单 {WaybillNum} 的三段码", waybillNum);

            var url = "/api/openApi/realTimeQuery";
            var request = BenNiaoSignHelper.CreateRequest(
                _config.BenNiaoAppId,
                _config.BenNiaoAppSecret,
                new { waybillNum });

            var response = await _httpClient.PostAsJsonAsync(url, request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<BenNiaoResponse<string>>();
            if (result is { IsSuccess: true }) return result.Result;
            Log.Error("实时查询三段码失败：{Message}", result?.Message);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "实时查询运单 {WaybillNum} 的三段码时发生错误", waybillNum);
            return null;
        }
    }

    /// <summary>
    ///     上传包裹数据
    /// </summary>
    private async Task<(bool Success, DateTime UploadTime)> UploadPackageDataAsync(PackageInfo package)
    {
        try
        {
            Log.Information("开始上传包裹 {Barcode} 的数据", package.Barcode);

            // 检查必要的数据
            if (!package.Length.HasValue || !package.Width.HasValue || !package.Height.HasValue)
            {
                Log.Warning("包裹 {Barcode} 缺少必要的数据（重量或尺寸），无法上传", package.Barcode);
                return (false, DateTime.MinValue);
            }

            var uploadTime = DateTime.Now;
            var url = "/api/openApi/dataUpload";
            var uploadItem = new DataUploadItem
            {
                WaybillNum = package.Barcode,
                ScanTime = uploadTime.ToString("yyyy-MM-dd HH:mm:ss"),
                Weight = Math.Round(Convert.ToDecimal(package.Weight) / 1000m, 2), // 转换为千克并保留2位小数
                GoodsLength = (int)Math.Round(package.Length.Value / 10.0), // 转换为厘米
                GoodsWidth = (int)Math.Round(package.Width.Value / 10.0), // 转换为厘米
                GoodsHeight = (int)Math.Round(package.Height.Value / 10.0), // 转换为厘米
                DeviceId = _config.DeviceId // 添加设备号
            };

            var request = BenNiaoSignHelper.CreateRequest(
                _config.BenNiaoAppId,
                _config.BenNiaoAppSecret,
                new[] { uploadItem });

            Log.Information("上传包裹数据请求：{@Request}", request);
            var response = await _httpClient.PostAsJsonAsync(url, request);

            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Information("上传包裹数据响应：{@Response}", responseContent);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<BenNiaoResponse<object>>();
            if (result == null || !result.IsSuccess)
            {
                Log.Error("上传包裹 {Barcode} 数据失败：{Message}", package.Barcode, result?.Message);
                return (false, DateTime.MinValue);
            }

            Log.Information("成功上传包裹 {Barcode} 的数据", package.Barcode);
            return (true, uploadTime);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "上传包裹 {Barcode} 数据时发生错误", package.Barcode);
            return (false, DateTime.MinValue);
        }
    }

    /// <summary>
    ///     上传图片到FTP服务器
    /// </summary>
    private async Task UploadImageAsync(string waybillNum, DateTime scanTime, string imagePath)
    {
        try
        {
            Log.Information("开始上传包裹 {WaybillNum} 的图片", waybillNum);

            // 检查FTP配置
            if (string.IsNullOrWhiteSpace(_config.BenNiaoFtpHost) ||
                string.IsNullOrWhiteSpace(_config.BenNiaoFtpUsername) ||
                string.IsNullOrWhiteSpace(_config.BenNiaoFtpPassword))
            {
                Log.Warning("FTP配置不完整，无法上传图片");
                return;
            }

            // 构建目标路径
            var dateDir = scanTime.ToString("yyyyMMdd");
            var fileName = $"{waybillNum}_{scanTime:yyyyMMddHHmmss}.jpg";

            // 创建FTP客户端
            using var ftpClient = new AsyncFtpClient(_config.BenNiaoFtpHost, _config.BenNiaoFtpUsername,
                _config.BenNiaoFtpPassword);

            try
            {
                await ftpClient.Connect();

                // 创建日期目录
                if (!await ftpClient.DirectoryExists(dateDir)) await ftpClient.CreateDirectory(dateDir);

                // 上传文件
                var remotePath = $"{dateDir}/{fileName}";
                var status = await ftpClient.UploadFile(imagePath, remotePath);

                if (status == FtpStatus.Success)
                    Log.Information("成功上传包裹 {WaybillNum} 的图片到 {RemotePath}", waybillNum, remotePath);
                else
                    Log.Warning("上传包裹 {WaybillNum} 的图片失败，状态：{Status}", waybillNum, status);
            }
            finally
            {
                if (ftpClient.IsConnected) await ftpClient.Disconnect();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "上传包裹 {WaybillNum} 的图片时发生错误", waybillNum);
        }
    }

    /// <summary>
    ///     将图片保存到临时文件
    /// </summary>
    private static async Task<string?> SaveImageToTempFileAsync(Image<Rgba32> image, string waybillNum,
        DateTime scanTime)
    {
        try
        {
            // 创建临时文件路径
            var tempDir = Path.Combine(Path.GetTempPath(), "BenNiao", "Images");
            Directory.CreateDirectory(tempDir);

            var fileName = $"{waybillNum}_{scanTime:yyyyMMddHHmmss}.jpg";
            var tempPath = Path.Combine(tempDir, fileName);

            // 保存图片
            await image.SaveAsJpegAsync(tempPath);
            Log.Information("已将包裹 {WaybillNum} 的图片保存到临时文件 {TempPath}", waybillNum, tempPath);

            return tempPath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存包裹 {WaybillNum} 的图片到临时文件时发生错误", waybillNum);
            return null;
        }
    }

    /// <summary>
    ///     异步上传包裹数据和图片
    /// </summary>
    private async Task UploadPackageDataAndImageAsync(PackageInfo package)
    {
        try
        {
            var (success, uploadTime) = await UploadPackageDataAsync(package);
            if (success && package.Image != null)
            {
                // 上传图片
                var tempImagePath = await SaveImageToTempFileAsync(package.Image, package.Barcode, uploadTime);
                if (!string.IsNullOrWhiteSpace(tempImagePath))
                {
                    await UploadImageAsync(package.Barcode, uploadTime, tempImagePath);
                    try
                    {
                        File.Delete(tempImagePath);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "删除临时图片文件 {TempImagePath} 失败", tempImagePath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "异步上传包裹 {Barcode} 数据和图片时发生错误", package.Barcode);
        }
    }
}
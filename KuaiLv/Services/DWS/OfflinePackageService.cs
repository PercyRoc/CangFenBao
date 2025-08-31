using Common.Data;
using Common.Models.Package;
using Serilog;

namespace KuaiLv.Services.DWS;

/// <summary>
///     离线包裹存储服务
/// </summary>
public class OfflinePackageService(IPackageDataService packageDataService)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    ///     保存离线包裹
    /// </summary>
    internal async Task SaveOfflinePackageAsync(PackageInfo package)
    {
        await _lock.WaitAsync();
        try
        {
            // 设置包裹状态为离线 - 使用 SetStatus
            package.SetStatus(PackageStatus.Offline, "网络断开，等待重试"); // 使用更明确的离线消息

            // 使用PackageDataService保存包裹
            await packageDataService.AddPackageAsync(package);

            Log.Information("包裹已保存到离线存储：{Barcode}", package.Barcode);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     获取所有（跨月份）离线包裹
    /// </summary>
    internal async Task<List<PackageInfo>> GetOfflinePackagesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            Log.Debug("开始查询所有离线包裹记录...");
            // 调用新的服务方法获取所有月份的离线包裹记录
            var offlineRecords = await packageDataService.GetAllOfflinePackagesAsync();
            Log.Information("查询到 {Count} 条离线包裹记录", offlineRecords.Count);

            // 转换为PackageInfo对象，排除"noread"条码的包裹
            var packages = new List<PackageInfo>();
            foreach (var record in offlineRecords)
            {
                // 跳过"noread"条码的包裹
                if (string.IsNullOrEmpty(record.Barcode) ||
                    record.Barcode.Equals("noread", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Debug("跳过noread条码的离线包裹：{Barcode}", record.Barcode);
                    continue;
                }

                var package = PackageInfo.Create();

                package.SetBarcode(record.Barcode);

                package.SetWeight(record.Weight);
                if (record is { Length: not null, Width: not null, Height: not null })
                    package.SetDimensions(record.Length.Value, record.Width.Value, record.Height.Value);
                // 直接使用数据库的状态和显示文本
                package.SetStatus(record.Status, record.StatusDisplay);

                if (!string.IsNullOrEmpty(record.ErrorMessage)) package.ErrorMessage = record.ErrorMessage;
                package.CreateTime = record.CreateTime;

                // 设置图片路径
                if (!string.IsNullOrEmpty(record.ImagePath)) package.SetImage(null, record.ImagePath);
                packages.Add(package);
            }

            return packages;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "查询所有离线包裹时发生错误");
            return []; // 发生错误时返回空列表
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     标记已成功上传的离线包裹状态
    /// </summary>
    /// <param name="barcode">包裹条码</param>
    /// <param name="packageTime">可选的包裹时间，用于跨表查找</param>
    internal async Task MarkOfflinePackageAsUploadedAsync(string barcode, DateTime? packageTime = null)
    {
        await _lock.WaitAsync();
        try
        {
            // 如果未提供包裹时间，可能需要先查询包裹信息
            if (packageTime == null)
            {
                // 查询包裹记录以获取其创建时间
                var record = await packageDataService.GetPackageByBarcodeAsync(barcode);
                if (record != null)
                {
                    packageTime = record.CreateTime;
                    Log.Debug("获取到包裹 {Barcode} 的创建时间: {CreateTime}", barcode, packageTime);
                }
            }

            // 调用更新状态的方法，传递包裹时间以辅助查找
            var success = await packageDataService.UpdatePackageStatusAsync(barcode,
                PackageStatus.Success,
                "离线重试成功",
                packageTime);

            if (success)
                Log.Information("离线包裹已成功标记为上传状态：{Barcode}", barcode);
            else
                // UpdatePackageStatusAsync 内部已经记录了更详细的 Warning 或 Error
                Log.Warning("未能标记离线包裹 {Barcode} 为上传状态（可能未找到记录或更新出错）", barcode);
        }
        catch (Exception ex)
        {
            // 捕获调用 UpdatePackageStatusAsync 时可能出现的意外异常
            Log.Error(ex, "在调用 UpdatePackageStatusAsync 标记离线包裹 {Barcode} 为成功时发生意外错误", barcode);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     标记离线包裹的重试已完成（无论成功还是失败，都不再重试）
    /// </summary>
    /// <param name="barcode">包裹条码</param>
    /// <param name="packageTime">包裹时间，用于跨表查找</param>
    /// <param name="success">是否成功</param>
    internal async Task MarkOfflinePackageAsRetryCompletedAsync(string barcode, DateTime? packageTime, bool success)
    {
        await _lock.WaitAsync();
        try
        {
            // 如果未提供包裹时间，可能需要先查询包裹信息
            if (packageTime == null)
            {
                // 查询包裹记录以获取其创建时间
                var record = await packageDataService.GetPackageByBarcodeAsync(barcode);
                if (record != null)
                {
                    packageTime = record.CreateTime;
                    Log.Debug("获取到包裹 {Barcode} 的创建时间: {CreateTime}", barcode, packageTime);
                }
            }

            // 根据成功或失败设置不同的状态和消息
            PackageStatus finalStatus;
            string statusMessage;

            if (success)
            {
                finalStatus = PackageStatus.Success;
                statusMessage = "离线重试成功";
            }
            else
            {
                finalStatus = PackageStatus.RetryCompleted;
                statusMessage = "离线重试失败，不再重试";
            }

            // 调用更新状态的方法
            var updateSuccess = await packageDataService.UpdatePackageStatusAsync(barcode,
                finalStatus,
                statusMessage,
                packageTime);

            if (updateSuccess)
                Log.Information("离线包裹 {Barcode} 已标记为重试完成状态：{Status}", barcode, finalStatus);
            else
                Log.Warning("未能标记离线包裹 {Barcode} 为重试完成状态（可能未找到记录或更新出错）", barcode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "在标记离线包裹 {Barcode} 为重试完成时发生意外错误", barcode);
        }
        finally
        {
            _lock.Release();
        }
    }
}
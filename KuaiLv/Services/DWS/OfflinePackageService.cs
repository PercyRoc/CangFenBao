using Common.Data;
using Common.Models.Package;
using Serilog;

namespace Presentation_KuaiLv.Services.DWS;

/// <summary>
///     离线包裹存储服务
/// </summary>
public class OfflinePackageService(IPackageDataService packageDataService)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    ///     保存离线包裹
    /// </summary>
    public async Task SaveOfflinePackageAsync(PackageInfo package)
    {
        await _lock.WaitAsync();
        try
        {
            // 设置包裹状态为离线
            package.Status = PackageStatus.Offline;

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
    ///     获取所有离线包裹
    /// </summary>
    public async Task<List<PackageInfo>> GetOfflinePackagesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            // 获取所有状态为离线的包裹
            var offlineRecords = await packageDataService.GetPackagesByStatusAsync(PackageStatus.Offline);

            // 转换为PackageInfo对象
            var packages = offlineRecords.Select(record => new PackageInfo
            {
                Barcode = record.Barcode,
                Weight = record.Weight,
                Length = record.Length,
                Width = record.Width,
                Height = record.Height,
                Volume = record.Volume,
                CreateTime = record.CreateTime,
                Status = record.Status
            }).ToList();

            return packages;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     删除已处理的离线包裹
    /// </summary>
    public async Task DeleteOfflinePackageAsync(string barcode)
    {
        await _lock.WaitAsync();
        try
        {
            // 获取包裹记录
            var record = await packageDataService.GetPackageByBarcodeAsync(barcode);
            if (record != null)
            {
                // 更新包裹状态为已处理
                record.Status = PackageStatus.Processed;
                await packageDataService.AddPackageAsync(new PackageInfo
                {
                    Barcode = record.Barcode,
                    Weight = record.Weight,
                    Length = record.Length,
                    Width = record.Width,
                    Height = record.Height,
                    Volume = record.Volume,
                    CreateTime = record.CreateTime,
                    Status = record.Status
                });

                Log.Information("离线包裹已标记为已处理：{Barcode}", barcode);
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
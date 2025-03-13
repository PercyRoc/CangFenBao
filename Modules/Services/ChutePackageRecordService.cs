using System.Collections.Concurrent;
using Common.Models.Package;
using Serilog;

namespace Presentation_Modules.Services;

/// <summary>
/// 格口包裹记录服务，用于记录每个格口分配的包裹，并在格口锁定时将数据上传到指定接口并清空
/// </summary>
public class ChutePackageRecordService
{
    // 使用线程安全的字典来存储每个格口的包裹记录
    private readonly ConcurrentDictionary<int, List<PackageInfo>> _chutePackages = new();
    
    // 格口锁定状态字典
    private readonly ConcurrentDictionary<int, bool> _chuteLockStatus = new();
    
    /// <summary>
    /// 添加包裹记录
    /// </summary>
    /// <param name="package">包裹信息</param>
    public void AddPackageRecord(PackageInfo package)
    {
        try
        {
            // 获取格口号
            var chuteNumber = package.ChuteName;
            
            // 如果格口已锁定，不记录
            if (IsChuteLocked(chuteNumber))
            {
                Log.Warning("格口 {ChuteNumber} 已锁定，不记录包裹 {Barcode}", chuteNumber, package.Barcode);
                return;
            }
            
            // 获取或创建格口包裹列表
            var packages = _chutePackages.GetOrAdd(chuteNumber, _ => new List<PackageInfo>());
            
            // 添加包裹记录
            lock (packages)
            {
                packages.Add(package);
                Log.Debug("格口 {ChuteNumber} 添加包裹记录: {Barcode}, 当前记录数: {Count}", 
                    chuteNumber, package.Barcode, packages.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "添加包裹记录时出错: {Barcode}", package.Barcode);
        }
    }
    
    /// <summary>
    /// 设置格口锁定状态
    /// </summary>
    /// <param name="chuteNumber">格口号</param>
    /// <param name="isLocked">是否锁定</param>
    public async Task SetChuteLockStatusAsync(int chuteNumber, bool isLocked)
    {
        try
        {
            // 更新格口锁定状态
            _chuteLockStatus[chuteNumber] = isLocked;
            
            // 如果格口被锁定，上传数据并清空
            if (isLocked)
            {
                await UploadAndClearChuteDataAsync(chuteNumber);
            }
            
            Log.Information("格口 {ChuteNumber} 锁定状态设置为: {Status}", 
                chuteNumber, isLocked ? "锁定" : "解锁");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设置格口 {ChuteNumber} 锁定状态时出错", chuteNumber);
        }
    }
    
    /// <summary>
    /// 上传并清空格口数据
    /// </summary>
    /// <param name="chuteNumber">格口号</param>
    private async Task UploadAndClearChuteDataAsync(int chuteNumber)
    {
        try
        {
            // 获取格口包裹列表
            if (!_chutePackages.TryGetValue(chuteNumber, out var packages) || packages.Count == 0)
            {
                Log.Information("格口 {ChuteNumber} 没有包裹记录，无需上传", chuteNumber);
                return;
            }
            
            // 复制包裹列表，避免并发修改
            List<PackageInfo> packagesCopy;
            lock (packages)
            {
                packagesCopy = new List<PackageInfo>(packages);
                packages.Clear();
            }
            
            // 上传数据到指定接口
            await UploadPackagesToApiAsync(chuteNumber, packagesCopy);
            
            Log.Information("格口 {ChuteNumber} 的 {Count} 条包裹记录已上传并清空", 
                chuteNumber, packagesCopy.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "上传并清空格口 {ChuteNumber} 数据时出错", chuteNumber);
        }
    }
    
    /// <summary>
    /// 上传包裹数据到API
    /// </summary>
    /// <param name="chuteNumber">格口号</param>
    /// <param name="packages">包裹列表</param>
    private async Task UploadPackagesToApiAsync(int chuteNumber, List<PackageInfo> packages)
    {
        try
        {
            // TODO: 实现上传到指定接口的逻辑
            // 这里是一个示例，实际实现需要根据接口规范来开发
            
            Log.Information("准备上传格口 {ChuteNumber} 的 {Count} 条包裹记录到API", 
                chuteNumber, packages.Count);
            
            // 模拟API调用延迟
            await Task.Delay(500);
            
            // 记录上传的包裹条码
            var barcodes = string.Join(", ", packages.Select(p => p.Barcode));
            Log.Information("格口 {ChuteNumber} 的包裹记录已上传: {Barcodes}", chuteNumber, barcodes);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "上传格口 {ChuteNumber} 的包裹数据到API时出错", chuteNumber);
            throw;
        }
    }
    
    /// <summary>
    /// 获取格口锁定状态
    /// </summary>
    /// <param name="chuteNumber">格口号</param>
    /// <returns>是否锁定</returns>
    private bool IsChuteLocked(int chuteNumber)
    {
        return _chuteLockStatus.TryGetValue(chuteNumber, out var isLocked) && isLocked;
    }
    
    /// <summary>
    /// 获取格口包裹记录数量
    /// </summary>
    /// <param name="chuteNumber">格口号</param>
    /// <returns>包裹记录数量</returns>
    public int GetChutePackageCount(int chuteNumber)
    {
        if (!_chutePackages.TryGetValue(chuteNumber, out var packages)) return 0;
        lock (packages)
        {
            return packages.Count;
        }

    }
    
    /// <summary>
    /// 获取所有格口的包裹记录数量
    /// </summary>
    /// <returns>格口包裹记录数量字典</returns>
    public Dictionary<int, int> GetAllChutePackageCounts()
    {
        var result = new Dictionary<int, int>();
        
        foreach (var kvp in _chutePackages)
        {
            lock (kvp.Value)
            {
                result[kvp.Key] = kvp.Value.Count;
            }
        }
        
        return result;
    }
} 
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Common.Models.Package;
using Common.Services.Settings;
using Serilog;
using ShanghaiModuleBelt.Models;

namespace ShanghaiModuleBelt.Services;

/// <summary>
///     格口包裹记录服务，用于记录每个格口分配的包裹，并在格口锁定时将数据上传到指定接口并清空
/// </summary>
public class ChutePackageRecordService(HttpClient httpClient, ISettingsService settingsService)
{
    private const string ApiUrl = "http://123.56.22.107:8080/api/DWSInfo2";

    // 格口锁定状态字典
    private readonly ConcurrentDictionary<int, bool> _chuteLockStatus = new();

    // 使用线程安全的字典来存储每个格口的包裹记录
    private readonly ConcurrentDictionary<int, List<PackageInfo>> _chutePackages = new();
    private readonly ModuleConfig _config = settingsService.LoadSettings<ModuleConfig>();

    /// <summary>
    ///     添加包裹记录
    /// </summary>
    /// <param name="package">包裹信息</param>
    internal void AddPackageRecord(PackageInfo package)
    {
        try
        {
            // 获取格口号
            var chuteNumber = package.ChuteNumber;

            // 如果格口已锁定，不记录
            if (IsChuteLocked(chuteNumber))
            {
                Log.Warning("格口 {ChuteNumber} 已锁定，不记录包裹 {Barcode}", chuteNumber, package.Barcode);
                return;
            }

            // 获取或创建格口包裹列表
            var packages = _chutePackages.GetOrAdd(chuteNumber, static _ => []);

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
    ///     设置格口锁定状态
    /// </summary>
    /// <param name="chuteNumber">格口号</param>
    /// <param name="isLocked">是否锁定</param>
    internal async Task SetChuteLockStatusAsync(int chuteNumber, bool isLocked)
    {
        try
        {
            // 更新格口锁定状态
            _chuteLockStatus[chuteNumber] = isLocked;

            // 如果格口被锁定，上传数据并清空
            if (isLocked) await UploadAndClearChuteDataAsync(chuteNumber);

            Log.Information("格口 {ChuteNumber} 锁定状态设置为: {Status}",
                chuteNumber, isLocked ? "锁定" : "解锁");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设置格口 {ChuteNumber} 锁定状态时出错", chuteNumber);
        }
    }

    /// <summary>
    ///     上传并清空格口数据
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
    ///     上传包裹数据到API
    /// </summary>
    /// <param name="chuteNumber">格口号</param>
    /// <param name="packages">包裹列表</param>
    private async Task UploadPackagesToApiAsync(int chuteNumber, IEnumerable<PackageInfo> packages)
    {
        try
        {
            // 根据站点代码确定handlers
            var handlers = _config.SiteCode;

            // 构建包裹条码字符串，用逗号分隔
            var packageCodes = string.Join(",", packages.Select(static p => p.Barcode));

            // 构建请求数据
            var requestData = new
            {
                packageCode = packageCodes,
                chute_code = chuteNumber.ToString(),
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                handlers,
                siteCode = _config.SiteCode
            };

            // 序列化为JSON
            var content = new StringContent(
                JsonSerializer.Serialize(requestData),
                Encoding.UTF8,
                "application/json");

            // 设置超时时间
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_config.ServerTimeout));

            // 发送请求
            Log.Information("正在上传格口 {ChuteNumber} 的包裹记录到API: {PackageCodes}",
                chuteNumber, packageCodes);

            // 设置请求头
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Add("equickToken", _config.Token);
            request.Content = content;
            
            var response = await httpClient.SendAsync(request, cts.Token);

            // 检查响应状态
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("上传格口包裹记录失败: HTTP状态码 {StatusCode}", response.StatusCode);
                throw new HttpRequestException($"HTTP状态码: {response.StatusCode}");
            }

            // 解析响应
            var responseJson = await response.Content.ReadAsStringAsync(cts.Token);
            var responseData = JsonSerializer.Deserialize<ApiResponse>(responseJson);

            if (responseData == null)
            {
                Log.Warning("解析API响应失败: {Response}", responseJson);
                throw new JsonException("无法解析API响应");
            }

            // 检查响应码
            if (responseData.Code != 200)
            {
                Log.Warning("上传格口包裹记录失败: 错误码 {Code}, 消息 {Message}",
                    responseData.Code, responseData.Msg);
                throw new Exception($"API错误: {responseData.Msg}");
            }

            Log.Information("格口 {ChuteNumber} 的包裹记录已成功上传", chuteNumber);
        }
        catch (TaskCanceledException)
        {
            Log.Warning("上传格口 {ChuteNumber} 的包裹记录超时, 超时时间: {Timeout}ms",
                chuteNumber, _config.ServerTimeout);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "上传格口 {ChuteNumber} 的包裹记录时发生异常", chuteNumber);
            throw;
        }
    }

    /// <summary>
    ///     获取格口锁定状态
    /// </summary>
    /// <param name="chuteNumber">格口号</param>
    /// <returns>是否锁定</returns>
    private bool IsChuteLocked(int chuteNumber)
    {
        return _chuteLockStatus.TryGetValue(chuteNumber, out var isLocked) && isLocked;
    }

    /// <summary>
    ///     获取格口包裹记录数量
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
    ///     获取所有格口的包裹记录数量
    /// </summary>
    /// <returns>格口包裹记录数量字典</returns>
    public Dictionary<int, int> GetAllChutePackageCounts()
    {
        var result = new Dictionary<int, int>();

        foreach (var kvp in _chutePackages)
            lock (kvp.Value)
            {
                result[kvp.Key] = kvp.Value.Count;
            }

        return result;
    }

    /// <summary>
    ///     API响应数据结构
    /// </summary>
    private class ApiResponse
    {
        [JsonPropertyName("result")] public string? Result { get; init; }

        [JsonPropertyName("code")] public int Code { get; init; }

        [JsonPropertyName("msg")] public string? Msg { get; init; }

        [JsonPropertyName("barCode")] public string? BarCode { get; init; }
    }
}
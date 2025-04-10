using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using ZtCloudWarehous.Models;

namespace ZtCloudWarehous.Utils;

/// <summary>
///     签名工具类
/// </summary>
internal static class SignatureHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    ///     计算签名
    /// </summary>
    /// <param name="commonParams">公共参数字典</param>
    /// <param name="businessParams">业务参数对象</param>
    /// <param name="secret">密钥</param>
    /// <returns>签名</returns>
    internal static string CalculateSignature(IDictionary<string, string> commonParams, object businessParams,
        string secret)
    {
        try
        {
            // 1. 按ASCII顺序排序公共参数
            var sortedParams = new SortedDictionary<string, string>(commonParams);

            // 2. 拼接公共参数（参数名+参数值）
            var stringBuilder = new StringBuilder();
            foreach (var param in sortedParams.Where(static param => !string.IsNullOrEmpty(param.Value)))
            {
                stringBuilder.Append(param.Key).Append(param.Value);
            }

            // 3. 添加业务参数JSON字符串
            var businessJson = JsonSerializer.Serialize(businessParams, JsonOptions);
            stringBuilder.Append(businessJson);

            // 4. 在字符串前后添加密钥
            var finalString = $"{secret}{stringBuilder}{secret}";

            // 5. MD5加密
            var inputBytes = Encoding.UTF8.GetBytes(finalString);
            var hashBytes = MD5.HashData(inputBytes);

            // 6. 转换为大写的十六进制字符串
            var sb = new StringBuilder();
            foreach (var b in hashBytes) sb.Append(b.ToString("X2"));

            return sb.ToString();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "计算签名时发生错误");
            throw;
        }
    }

    /// <summary>
    ///     获取公共参数
    /// </summary>
    /// <param name="request">请求对象</param>
    /// <returns>公共参数字典</returns>
    internal static Dictionary<string, string> GetCommonParameters(WeighingRequest request)
    {
        return new Dictionary<string, string>
        {
            ["api"] = request.Api,
            ["appkey"] = request.AppKey,
            ["customerId"] = request.CustomerId
        };
    }

    /// <summary>
    ///     获取业务参数
    /// </summary>
    /// <param name="request">请求对象</param>
    /// <returns>业务参数对象</returns>
    internal static Dictionary<string, object> GetBusinessParameters(WeighingRequest request)
    {
        var businessParams = new Dictionary<string, object>
        {
            ["tenantId"] = request.TenantId,
            ["warehouseCode"] = request.WarehouseCode,
            ["waybillCode"] = request.WaybillCode,
            ["packagingMaterialCode"] = request.PackagingMaterialCode,
            ["actualVolume"] = request.ActualVolume,
            ["actualWeight"] = request.ActualWeight,
            ["userRealName"] = request.UserRealName
        };

        return businessParams;
    }
}
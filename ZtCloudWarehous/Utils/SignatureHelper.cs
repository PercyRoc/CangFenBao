using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using ZtCloudWarehous.Models;

namespace ZtCloudWarehous.Utils;

/// <summary>
///     签名工具类
/// </summary>
internal static class SignatureHelper
{
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
        // 1. 按ASCII顺序排序公共参数
        var sortedParams = new SortedDictionary<string, string>(commonParams);

        // 2. 拼接公共参数
        var stringBuilder = new StringBuilder();
        foreach (var param in sortedParams.Where(static param => !string.IsNullOrEmpty(param.Value)))
            stringBuilder.Append(param.Key).Append(param.Value);

        // 3. 添加业务参数JSON字符串
        var businessJson = JsonSerializer.Serialize(businessParams);
        stringBuilder.Append(businessJson);

        // 4. 在字符串前后添加密钥
        var finalString = $"{secret}{stringBuilder}{secret}";

        // 5. MD5加密
        using var md5 = MD5.Create();
        var inputBytes = Encoding.UTF8.GetBytes(finalString);
        var hashBytes = md5.ComputeHash(inputBytes);

        // 6. 转换为大写的十六进制字符串
        var sb = new StringBuilder();
        foreach (var b in hashBytes) sb.Append(b.ToString("X2"));

        return sb.ToString();
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
            ["companyCode"] = request.CompanyCode,
            ["appkey"] = request.AppKey,
            ["timestamp"] = request.Timestamp,
            ["sign_method"] = request.SignMethod,
            ["v"] = request.Version
        };
    }

    /// <summary>
    ///     获取业务参数
    /// </summary>
    /// <param name="request">请求对象</param>
    /// <returns>业务参数对象</returns>
    internal static object GetBusinessParameters(WeighingRequest request)
    {
        return new
        {
            tenantId = request.TenantId,
            warehouseCode = request.WarehouseCode,
            waybillCode = request.WaybillCode,
            packagingMaterialCode = request.PackagingMaterialCode,
            actualVolume = request.ActualVolume,
            actualWeight = request.ActualWeight,
            weighingEquipment = request.WeighingEquipment,
            userId = request.UserId,
            userRealName = request.UserRealName
        };
    }

    /// <summary>
    ///     将对象转换为参数字典
    /// </summary>
    /// <param name="obj">要转换的对象</param>
    /// <returns>参数字典</returns>
    public static Dictionary<string, string> ObjectToParameters(object obj)
    {
        var parameters = new Dictionary<string, string>();
        var jsonElement = JsonSerializer.SerializeToElement(obj);

        if (jsonElement.ValueKind != JsonValueKind.Object) return parameters;

        foreach (var property in jsonElement.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;

            var value = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.ToString();

            if (!string.IsNullOrEmpty(value)) parameters[property.Name] = value;
        }

        return parameters;
    }

    /// <summary>
    ///     将参数字典转换为URL查询字符串
    /// </summary>
    /// <param name="parameters">参数字典</param>
    /// <returns>URL查询字符串</returns>
    public static string ToQueryString(IDictionary<string, string> parameters)
    {
        var sortedParams = new SortedDictionary<string, string>(parameters);
        var pairs = sortedParams
            .Select(static p => $"{HttpUtility.UrlEncode(p.Key)}={HttpUtility.UrlEncode(p.Value)}");
        return string.Join("&", pairs);
    }
}
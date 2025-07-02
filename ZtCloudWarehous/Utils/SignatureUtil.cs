using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using ZtCloudWarehous.Models;

namespace ZtCloudWarehous.Utils;

/// <summary>
///     签名工具类
/// </summary>
internal static class SignatureUtil
{
    /// <summary>
    ///     生成MD5签名
    /// </summary>
    /// <param name="parameters">参数字典</param>
    /// <param name="aesKey">AES密钥</param>
    /// <param name="isImageUpload">是否是图片上传</param>
    /// <returns>MD5签名</returns>
    internal static string GenerateMd5Signature(IDictionary<string, string> parameters, string aesKey,
        bool isImageUpload = false)
    {
        try
        {
            // 构建签名字符串
            var sb = new StringBuilder();

            // 特殊处理 data 参数，将 JSON 格式转换为指定格式
            if (parameters.TryGetValue("data", out var dataJson))
            {
                if (isImageUpload)
                {
                    var request = JsonSerializer.Deserialize<WaybillImageUploadRequest>(dataJson);
                    if (request?.Data.Count > 0)
                    {
                        var waybill = request.Data[0];
                        sb.Append("data=[{");
                        sb.Append($"waybillNumber={waybill.WaybillNumber}, ");
                        sb.Append($"weightTime={waybill.WeightTime}");
                        sb.Append("}]");
                    }
                }
                else
                {
                    var request = JsonSerializer.Deserialize<WaybillUploadRequest>(dataJson);
                    if (request?.Data.Count > 0)
                    {
                        var waybill = request.Data[0];
                        sb.Append("data=[{");
                        sb.Append($"jtHistoryWeight={waybill.JtHistoryWeight}, ");
                        sb.Append($"jtWaybillSize={waybill.JtWaybillSize}, ");
                        sb.Append($"jtWaybillVolume={waybill.JtWaybillVolume}, ");
                        sb.Append($"waybillNumber={waybill.WaybillNumber}, ");
                        sb.Append($"weight={waybill.Weight}, ");
                        sb.Append($"weightTime={waybill.WeightTime}");
                        sb.Append("}]");
                    }
                }
            }

            // 添加其他参数
            if (parameters.TryGetValue("machineMx", out var machineMx)) sb.Append("&machineMx=").Append(machineMx);
            if (parameters.TryGetValue("timestamp", out var timestamp)) sb.Append("&timestamp=").Append(timestamp);

            // 添加AES密钥
            sb.Append('&').Append(aesKey);

            // 记录签名源字符串，方便调试
            Log.Debug("签名源字符串: {SignString}", sb.ToString());

            // 计算MD5
            var inputBytes = Encoding.UTF8.GetBytes(sb.ToString());
            var hashBytes = MD5.HashData(inputBytes);

            // 转换为小写的十六进制字符串
            var result = new StringBuilder();
            foreach (var b in hashBytes) result.Append(b.ToString("x2"));

            return result.ToString();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "生成MD5签名时发生错误");
            throw;
        }
    }
}
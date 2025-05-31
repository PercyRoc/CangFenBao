using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Common.Services.Settings;
using Serilog;
using Server.YouYu.Models;

namespace Server.YouYu.Services
{
    public class YouYuService(HttpClient httpClient, ISettingsService settingsService) : IYouYuService
    {
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true, // 接口返回的字段首字母小写，需要不区分大小写
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // 接口返回的字段首字母小写，需要不区分大小写

        public async Task<string?> GetSegmentCodeAsync(string barcode)
        {
            try
            {
                var config = settingsService.LoadSettings<SegmentCodeUrlConfig>();

                if (string.IsNullOrWhiteSpace(config.Url) || string.IsNullOrWhiteSpace(config.AppId) || string.IsNullOrWhiteSpace(config.AppKey))
                {
                    Log.Error("右玉接口配置不完整，请检查 Url, AppId, AppKey 是否均已配置。");
                    return null;
                }

                var bodyObj = new { sheetNo = barcode };
                var bodyStr = JsonSerializer.Serialize(bodyObj);
                var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
                var sign = CalculateSign(config.AppId, bodyStr, timestamp, config.AppKey);

                var request = new HttpRequestMessage(HttpMethod.Post, config.Url)
                {
                    Content = new StringContent(bodyStr, Encoding.UTF8, "application/json")
                };

                request.Headers.Add("ai-box", "1");
                request.Headers.Add("appid", config.AppId);
                request.Headers.Add("timestamp", timestamp);
                request.Headers.Add("sign", sign);

                Log.Debug("请求右玉接口：URL={Url}, Headers={@Headers}, Body={Body}", 
                    config.Url, 
                    request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)),
                    bodyStr);

                var response = await httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();

                Log.Debug("右玉接口响应：StatusCode={StatusCode}, Content={Content}", response.StatusCode, content);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning("调用右玉接口失败：StatusCode={StatusCode}, URL={Url}", response.StatusCode, config.Url);
                    return null;
                }
                
                var jsonResponse = JsonSerializer.Deserialize<YouYuResponse>(content, _jsonOptions);
                if (jsonResponse?.Code == 0)
                {
                    if (jsonResponse.Data != null && !string.IsNullOrWhiteSpace(jsonResponse.Data.BoxNo))
                    {
                        Log.Information("成功获取右玉格口号：{BoxNo}, 响应时间：{ResponseTime}ms, 条码: {Barcode}",
                            jsonResponse.Data.BoxNo, jsonResponse.Data.ResponseTime, barcode);
                        return jsonResponse.Data.BoxNo;
                    }

                    Log.Warning("右玉接口返回格口号为空：ResponseData={@Data}, Barcode={Barcode}", jsonResponse.Data, barcode);
                    return null;
                }

                Log.Warning("获取右玉格口号失败：Code={Code}, Message={Message}, URL={Url}, Barcode={Barcode}",
                    jsonResponse?.Code, jsonResponse?.Message, config.Url, barcode);
                return null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "调用右玉接口异常：Barcode={Barcode}", barcode);
                return null;
            }
        }

        private static string CalculateSign(string appid, string bodyStr, string timestamp, string appKey)
        {
            var signStr = $"{appid}{bodyStr}{timestamp}{appKey}";
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(signStr));
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
    }
} 
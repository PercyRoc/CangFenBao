using System.Net.Http;
using System.Text.Json;
using Common.Models.Package;
using Common.Services.Settings;
using Rookie.Models.Api;
using Rookie.Models.Settings;
using Serilog;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.IO;

namespace Rookie.Services;

public class RookieApiService(HttpClient httpClient, ISettingsService settingsService) : IRookieApiService
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    private readonly ISettingsService _settingsService =
        settingsService ?? throw new ArgumentNullException(nameof(settingsService));

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy =
            JsonNamingPolicy.CamelCase, // Ensure compatibility if needed, though attributes handle this
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Command constants
    private const string CmdParcelInfoUpload = "sorter.parcel_info_upload";
    private const string CmdDestRequest = "sorter.dest_request";
    private const string CmdSortReport = "sorter.sort_report";

    private RookieApiSettings LoadSettings()
    {
        return _settingsService.LoadSettings<RookieApiSettings>()
               ?? throw new InvalidOperationException("无法加载 Rookie API 设置。");
    }

    private static long GenerateRequestId() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private async Task<TResponse?> SendCommandAsync<TResponse>(string command, object parameters)
        where TResponse : class
    {
        string requestUrl;
        RookieApiSettings settings; // Declare settings outside the try block
        try
        {
            settings = LoadSettings();
            if (string.IsNullOrWhiteSpace(settings.ApiBaseUrl))
            {
                Log.Error("Rookie API 基础 URL 未配置。");
                return null;
            }

            // Assuming the base URL is the full endpoint for POST requests
            requestUrl = settings.ApiBaseUrl;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送命令 {Command} 前加载 Rookie API 设置失败。", command);
            return null;
        }

        var requestId = GenerateRequestId();
        var request = new BaseRequest
        {
            Source = settings.Source,
            Version = 1,
            RequestId = requestId,
            Data =
            [
                new CommandData { Command = command, Params = parameters }
            ]
        };

        var requestJson = "{}"; // Default empty json
        try
        {
            requestJson = JsonSerializer.Serialize(request, _jsonOptions);
            Log.Debug("发送 Rookie API 请求 ({Command}): {Payload}", command, requestJson);

            // Consider adding a timeout via CancellationToken if needed
            using var content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(requestUrl, content);

            var responseBody = await response.Content.ReadAsStringAsync();
            Log.Debug("收到 Rookie API 响应 ({Command}): 状态={Status}, 内容={Body}", command, response.StatusCode,
                responseBody);

            response.EnsureSuccessStatusCode(); // Throws on non-2xx codes

            var baseResponse = JsonSerializer.Deserialize<BaseResponse>(responseBody, _jsonOptions);

            if (baseResponse == null || baseResponse.RequestId != requestId || baseResponse.Result.Count == 0)
            {
                Log.Error("收到无效或不匹配的响应，请求 ID {RequestId} ({Command})", requestId, command);
                return null;
            }

            var commandResult = baseResponse.Result.First(); // Assuming single command per request
            if (commandResult.Code != 0)
            {
                Log.Error("Rookie API 为请求 {RequestId} ({Command}) 返回错误: Code={Code}, Error='{Error}'",
                    requestId, command, commandResult.Code, commandResult.Error);
                return null;
            }

            if (typeof(TResponse) != typeof(object))
            {
                if (commandResult.Params.ValueKind == JsonValueKind.Null ||
                    commandResult.Params.ValueKind == JsonValueKind.Undefined)
                {
                    Log.Warning("命令 {Command} 的响应参数为空，无法反序列化为 {Type}", command, typeof(TResponse).Name);
                    return null;
                }

                return commandResult.Params.Deserialize<TResponse>(_jsonOptions);
            }

            return new object() as TResponse;
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "发送命令 {Command} 到 {Url} 时发生 HTTP 请求错误。", command, requestUrl);
            return null;
        }
        catch (JsonException ex)
        {
            Log.Error(ex, "命令 {Command} 发生 JSON 序列化/反序列化错误。 请求: {Request}", command, requestJson);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送命令 {Command} 时发生意外错误。", command);
            return null;
        }
    }


    public async Task<bool> UploadParcelInfoAsync(PackageInfo package)
    {
        RookieApiSettings settings;
        try
        {
            settings = LoadSettings();
        }
        catch (InvalidOperationException ex)
        {
            Log.Error(ex, "无法加载设置，无法上传包裹信息。包裹: {Barcode}", package.Barcode);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载设置时发生未知错误，无法上传包裹信息。包裹: {Barcode}", package.Barcode);
            return false;
        }

        var parameters = new ParcelInfoUploadParams
        {
            BcrName = settings.BcrName,
            BarCode = string.IsNullOrWhiteSpace(package.Barcode) ? "NoRead" : package.Barcode,
            BcrCode = settings.BcrCode,
            Weight = (long)(package.Weight * 1000),
            Height = package.Height.HasValue ? (long)(package.Height.Value * 10) : null,
            Width = package.Width.HasValue ? (long)(package.Width.Value * 10) : null,
            Length = package.Length.HasValue ? (long)(package.Length.Value * 10) : null,
            Volume = package.Volume.HasValue ? (long)(package.Volume.Value * 1000) : null,
            PictureOssPath = package.ImagePath
        };

        var result = await SendCommandAsync<object>(CmdParcelInfoUpload, parameters);
        if (result == null) Log.Warning("上传包裹信息失败，条码: {Barcode}", parameters.BarCode);
        return result != null;
    }

    public async Task<DestRequestResultParams?> RequestDestinationAsync(string barcode, string itemBarcode = "NoRead")
    {
        string requestBarcode = string.IsNullOrWhiteSpace(barcode) ? "NoRead" : barcode;

        RookieApiSettings settings;
        try
        {
            settings = LoadSettings();
        }
        catch (InvalidOperationException ex)
        {
            Log.Error(ex, "无法加载设置，无法请求目的地。条码: {Barcode}", requestBarcode);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载设置时发生未知错误，无法请求目的地。条码: {Barcode}", requestBarcode);
            return null;
        }

        var parameters = new DestRequestParams
        {
            BcrName = settings.BcrName,
            BarCode = requestBarcode,
            BcrCode = settings.BcrCode,
            ItemBarcode = itemBarcode
        };

        var resultParams = await SendCommandAsync<DestRequestResultParams>(CmdDestRequest, parameters);
        if (resultParams == null) Log.Warning("请求目的地失败，条码: {Barcode}", requestBarcode);
        return resultParams;
    }

    public async Task<bool> ReportSortResultAsync(string barcode, string chuteCode, bool success,
        string? errorReason = null)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            Log.Warning("无法上报分拣结果，包裹条码为空。格口: {ChuteCode}, 状态: {Success}", chuteCode, success);
            return false;
        }

        RookieApiSettings settings;
        try
        {
            settings = LoadSettings();
        }
        catch (InvalidOperationException ex)
        {
            Log.Error(ex, "无法加载设置，无法上报分拣结果。条码: {Barcode}", barcode);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载设置时发生未知错误，无法上报分拣结果。条码: {Barcode}", barcode);
            return false;
        }

        var parameters = new SortReportParams
        {
            BcrName = settings.BcrName,
            BarCode = barcode,
            ChuteCode = chuteCode,
            BcrCode = settings.BcrCode,
            Status = success ? 0 : 1,
            ErrorReason = success ? null : (errorReason ?? "分拣失败")
        };

        var result = await SendCommandAsync<object>(CmdSortReport, parameters);
        if (result == null)
            Log.Warning("上报分拣结果失败，条码: {Barcode}, 格口: {ChuteCode}, 状态: {Success}", barcode, chuteCode, success);
        return result != null;
    }

    // 签名工具类
    private static class TraceSignUtil
    {
        public const string DwsSignSecretKey = "U2FsdGVkX18eFJHvqtwiheqmfg==";
        private const string ConcatSeparator = "_";

        public static string Md5Sign(string signId, string key)
        {
            if (string.IsNullOrEmpty(signId) || string.IsNullOrEmpty(key))
                return string.Empty;
            using var md5 = MD5.Create();
            var input = signId + ConcatSeparator + key;
            var bytes = System.Text.Encoding.UTF8.GetBytes(input);
            var hash = md5.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }

    /// <summary>
    /// 上传图片文件，返回图片URL（失败返回null）
    /// </summary>
    /// <param name="filePath">本地图片文件路径</param>
    /// <returns>图片URL或null</returns>
    public async Task<string?> UploadImageAsync(string filePath)
    {
        // 中文注释：上传图片到OSS服务，返回图片URL或null
        var settings = LoadSettings();
        // 拼接上传URL
        var uploadUrl = settings.ImageUploadUrl?.TrimEnd('/') + "/receive/outer/dws/upload";
        var signId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var sign = TraceSignUtil.Md5Sign(signId, TraceSignUtil.DwsSignSecretKey);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(signId), "signId");
        form.Add(new StringContent(sign), "sign");
        await using var fileStream = File.OpenRead(filePath);
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", Path.GetFileName(filePath));

        using var response = await _httpClient.PostAsync(uploadUrl, form);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Log.Error("图片上传失败: {Status} {Body}", response.StatusCode, json);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // 只判断data.errCode=="0"且有url即可，不依赖success字段
            if (root.TryGetProperty("data", out var dataProp)
                && dataProp.TryGetProperty("errCode", out var errCodeProp)
                && errCodeProp.GetString() == "0"
                && dataProp.TryGetProperty("url", out var urlProp))
            {
                return urlProp.GetString();
            }
            Log.Error("图片上传响应异常: {Json}", json);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "解析图片上传响应失败: {Json}", json);
            return null;
        }
    }
}
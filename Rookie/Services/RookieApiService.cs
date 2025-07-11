using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Common.Models.Package;
using Common.Services.Settings;
using Rookie.Models.Api;
using Rookie.Models.Settings;
using Serilog;

namespace Rookie.Services;

public class RookieApiService : IRookieApiService
{
    // Command constants
    private const string CmdParcelInfoUpload = "sorter.parcel_info_upload";
    private const string CmdDestRequest = "sorter.dest_request";
    private const string CmdSortReport = "sorter.sort_report";
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ISettingsService _settingsService;


    public RookieApiService(HttpClient httpClient, ISettingsService settingsService)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

        // Configure JSON options (optional, but good practice)
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // Ensure compatibility if needed, though attributes handle this
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }


    public async Task<bool> UploadParcelInfoAsync(PackageInfo package)
    {
        RookieApiSettings settings;
        try { settings = LoadSettings(); }
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
        var requestBarcode = string.IsNullOrWhiteSpace(barcode) ? "NoRead" : barcode;

        RookieApiSettings settings;
        try { settings = LoadSettings(); }
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

    public async Task<bool> ReportSortResultAsync(string barcode, string chuteCode, bool success, string? errorReason = null)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            Log.Warning("无法上报分拣结果，包裹条码为空。格口: {ChuteCode}, 状态: {Success}", chuteCode, success);
            return false;
        }

        RookieApiSettings settings;
        try { settings = LoadSettings(); }
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
            ErrorReason = success ? null : errorReason ?? "分拣失败"
        };

        var result = await SendCommandAsync<object>(CmdSortReport, parameters);
        if (result == null) Log.Warning("上报分拣结果失败，条码: {Barcode}, 格口: {ChuteCode}, 状态: {Success}", barcode, chuteCode, success);
        return result != null;
    }

    private RookieApiSettings LoadSettings()
    {
        // Load settings on demand per call, following best practices
        return _settingsService.LoadSettings<RookieApiSettings>()
               ?? throw new InvalidOperationException("无法加载 Rookie API 设置。");
    }

    private long GenerateRequestId()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private async Task<TResponse?> SendCommandAsync<TResponse>(string command, object parameters) where TResponse : class
    {
        RookieApiSettings settings;
        string requestUrl;
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
            Source = "CangFenBaoWcs", // Or load from settings if needed
            Version = 1,
            RequestId = requestId,
            Data =
            [
                new CommandData
                {
                    Command = command,
                    Params = parameters
                }
            ]
        };

        var requestJson = "{}"; // Default empty json
        try
        {
            requestJson = JsonSerializer.Serialize(request, _jsonOptions);
            Log.Debug("发送 Rookie API 请求 ({Command}): {Payload}", command, requestJson);

            // Consider adding a timeout via CancellationToken if needed
            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(requestUrl, content);

            var responseBody = await response.Content.ReadAsStringAsync();
            Log.Debug("收到 Rookie API 响应 ({Command}): 状态={Status}, 内容={Body}", command, response.StatusCode, responseBody);

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
                if (commandResult.Params.ValueKind == JsonValueKind.Null || commandResult.Params.ValueKind == JsonValueKind.Undefined)
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
}
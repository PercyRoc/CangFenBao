using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Models.Package;
using Common.Services.Settings;
using PlateTurnoverMachine.Models;
using Serilog;

namespace PlateTurnoverMachine.Services;

/// <summary>
/// 中通分拣服务实现
/// </summary>
public class ZtoSortingService : IZtoSortingService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private bool _disposed;

    /// <summary>
    /// 中通分拣服务构造函数
    /// </summary>
    /// <param name="settingsService">设置服务</param>
    public ZtoSortingService(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        
        Log.Information("中通分拣服务已初始化");
    }

    /// <summary>
    /// 配置服务参数
    /// </summary>
    /// <param name="apiUrl">API地址</param>
    /// <param name="companyId">公司ID</param>
    /// <param name="secretKey">密钥</param>
    public void Configure(string apiUrl, string companyId, string secretKey)
    {
        var settings = _settingsService.LoadSettings<PlateTurnoverSettings>();
        settings.ZtoApiUrl = apiUrl;
        settings.ZtoCompanyId = companyId;
        settings.ZtoSecretKey = secretKey;
        _settingsService.SaveSettings(settings);
        
        Log.Information("已更新中通分拣服务配置并保存到设置中");
    }

    /// <summary>
    /// 上报流水线开停状态
    /// </summary>
    /// <param name="pipeline">分拣线编码</param>
    /// <param name="status">流水线状态: start | stop | synchronization</param>
    /// <returns>API响应</returns>
    public async Task<ZtoSortingBaseResponse?> ReportPipelineStatusAsync(string pipeline, string status)
    {
        try
        {
            var request = new PipelineStatusRequest
            {
                Pipeline = pipeline,
                Status = status,
                SwitchTime = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                SortMode = "Sorting"
            };

            var requestJson = JsonSerializer.Serialize(request);
            return await SendRequestAsync<ZtoSortingBaseResponse>("WCS_PIPELINE_STATUS", requestJson);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "上报流水线状态异常: {Status}", status);
            return new ZtoSortingBaseResponse
            {
                Status = false,
                Message = ex.Message
            };
        }
    }

    /// <summary>
    /// 获取分拣方案
    /// </summary>
    /// <param name="pipeline">分拣线编码</param>
    /// <returns>分拣方案列表</returns>
    public async Task<List<object>> GetSortingSettingAsync(string pipeline)
    {
        try
        {
            var request = new
            {
                pipeline
            };

            var requestJson = JsonSerializer.Serialize(request);
            var response = await SendRequestAsync<List<object>>("WCS_SORTING_SETTING", requestJson);
            return response ?? [];
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取分拣方案异常: {Pipeline}", pipeline);
            return [];
        }
    }

    /// <summary>
    /// 获取面单规则
    /// </summary>
    /// <returns>面单规则</returns>
    public async Task<BillRuleResponse> GetBillRuleAsync()
    {
        try
        {
            // 该接口data为null
            var response = await SendRequestAsync<BillRuleResponse>("GET_BILL_RULE", null);
            return response ?? new BillRuleResponse();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取面单规则异常");
            return new BillRuleResponse();
        }
    }

    /// <summary>
    /// 获取分拣信息
    /// </summary>
    /// <param name="billCode">运单编号</param>
    /// <param name="pipeline">分拣线编码</param>
    /// <param name="turnNumber">扫描次数</param>
    /// <param name="trayCode">小车编号</param>
    /// <param name="weight">重量</param>
    /// <returns>分拣信息</returns>
    public async Task<SortingInfoResponse> GetSortingInfoAsync(string billCode, string pipeline, int turnNumber, string trayCode = "", float? weight = null)
    {
        try
        {
            var request = new SortingInfoRequest
            {
                BillCode = billCode,
                Pipeline = pipeline,
                TurnNumber = turnNumber,
                RequestTime = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                TrayCode = trayCode,
                Weight = weight
            };

            var requestJson = JsonSerializer.Serialize(request);
            var response = await SendRequestAsync<SortingInfoResponse>("WCS_SORTING_INFO", requestJson);
            return response ?? new SortingInfoResponse();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取分拣信息异常: {Barcode}", billCode);
            return new SortingInfoResponse();
        }
    }

    /// <summary>
    /// 推送分拣结果
    /// </summary>
    /// <param name="package">包裹信息</param>
    /// <param name="pipeline">分拣线编码</param>
    /// <param name="turnNumber">扫描次数</param>
    /// <param name="trayCode">小车编号</param>
    /// <returns>分拣结果响应</returns>
    public async Task<SortingResultResponse> ReportSortingResultAsync(PackageInfo package, string pipeline, int turnNumber, string trayCode = "")
    {
        try
        {
            var request = new SortingResultRequest
            {
                BillCode = package.Barcode,
                Pipeline = pipeline,
                TurnNumber = turnNumber,
                SortTime = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                TrayCode = trayCode,
                SortPortCode = package.ChuteNumber.ToString("D3"), // 格式化为3位数字
                SortSource = package.Status == PackageStatus.Error ? "3" : "0" // 如果是异常状态，设置为异常件
            };

            var requestJson = JsonSerializer.Serialize(request);
            var response = await SendRequestAsync<SortingResultResponse>("WCS_SORTING_RESULT", requestJson);
            return response ?? new SortingResultResponse();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "推送分拣结果异常: {Barcode}", package.Barcode);
            return new SortingResultResponse();
        }
    }

    /// <summary>
    /// 校验服务器时间
    /// </summary>
    /// <returns>服务器时间</returns>
    public async Task<TimeInspectionResponse> InspectTimeAsync()
    {
        try
        {
            // 该接口data为null
            var response = await SendRequestAsync<TimeInspectionResponse>("ZTO_INSPECTION_TIME", null);
            return response ?? new TimeInspectionResponse();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "校验服务器时间异常");
            return new TimeInspectionResponse();
        }
    }

    /// <summary>
    /// 发送请求到中通服务
    /// </summary>
    /// <typeparam name="T">返回类型</typeparam>
    /// <param name="msgType">消息类型</param>
    /// <param name="data">请求数据JSON</param>
    /// <returns>响应数据</returns>
    private async Task<T?> SendRequestAsync<T>(string msgType, string? data)
    {
        try
        {
            // 直接从设置服务获取最新配置
            var settings = _settingsService.LoadSettings<PlateTurnoverSettings>();
            var apiUrl = settings.ZtoApiUrl;
            var companyId = settings.ZtoCompanyId;
            var secretKey = settings.ZtoSecretKey;
            
            // 构建请求参数
            var requestData = data ?? string.Empty;
            var dataDigest = CalculateMd5(requestData + secretKey);

            // 不使用URL编码，直接拼接请求字符串
            var requestBodyStr = $"data={requestData}&data_digest={dataDigest}&msg_type={msgType}&company_id={companyId}";
            
            // 使用@符号标记原始字符串，避免Serilog错误解析大括号
            Log.Debug("发送中通请求(不使用URL编码): {@RequestBody}", requestBodyStr);

            // 创建表单内容
            var content = new StringContent(requestBodyStr, Encoding.UTF8, "application/x-www-form-urlencoded");
            
            var response = await _httpClient.PostAsync(apiUrl, content);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            // 使用@符号标记原始字符串，避免Serilog错误解析大括号
            Log.Debug("中通响应: {@Response}", responseContent);

            return JsonSerializer.Deserialize<T>(responseContent);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送请求异常: {MsgType}", msgType);
            return default;
        }
    }

    /// <summary>
    /// 计算MD5
    /// </summary>
    /// <param name="input">输入字符串</param>
    /// <returns>MD5哈希值</returns>
    private static string CalculateMd5(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = MD5.HashData(bytes);
        var sb = new StringBuilder();

        foreach (var h in hash)
        {
            sb.Append(h.ToString("x2"));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    /// <param name="disposing">是否正在dispose</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        
        if (disposing)
        {
            _httpClient.Dispose();
        }
        
        _disposed = true;
    }
} 
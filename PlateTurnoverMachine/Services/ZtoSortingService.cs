using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Common.Models.Package;
using PlateTurnoverMachine.Models;
using Serilog;

namespace PlateTurnoverMachine.Services;

/// <summary>
/// 中通分拣服务实现
/// </summary>
public class ZtoSortingService : IZtoSortingService, IDisposable
{
    private readonly HttpClient _httpClient;
    private string _apiUrl = "https://intelligent-2nd-pro.zt-express.com/branchweb/sortservice";
    private string _companyId = string.Empty;
    private string _secretKey = string.Empty;
    private bool _disposed = false;

    /// <summary>
    /// 中通分拣服务构造函数
    /// </summary>
    public ZtoSortingService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// 配置服务参数
    /// </summary>
    /// <param name="apiUrl">API地址</param>
    /// <param name="companyId">公司ID</param>
    /// <param name="secretKey">密钥</param>
    public void Configure(string apiUrl, string companyId, string secretKey)
    {
        _apiUrl = apiUrl;
        _companyId = companyId;
        _secretKey = secretKey;
    }

    /// <summary>
    /// 上报流水线开停状态
    /// </summary>
    /// <param name="pipeline">分拣线编码</param>
    /// <param name="status">流水线状态: start | stop | synchronization</param>
    /// <returns>API响应</returns>
    public async Task<ZtoSortingBaseResponse> ReportPipelineStatusAsync(string pipeline, string status)
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
                Status = "false",
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
                pipeline = pipeline
            };

            var requestJson = JsonSerializer.Serialize(request);
            var response = await SendRequestAsync<List<object>>("WCS_SORTING_SETTING", requestJson);
            return response ?? new List<object>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取分拣方案异常: {Pipeline}", pipeline);
            return new List<object>();
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
            // 构建请求参数
            var requestData = data ?? string.Empty;
            var dataDigest = CalculateMd5(requestData + _secretKey);

            var request = new
            {
                data = requestData,
                data_digest = dataDigest,
                msg_type = msgType,
                company_id = _companyId
            };

            var json = JsonSerializer.Serialize(request);
            Log.Debug("发送中通请求: {Request}", json);

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(_apiUrl, content);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            Log.Debug("中通响应: {Response}", responseContent);

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
        using var md5 = MD5.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = md5.ComputeHash(bytes);
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
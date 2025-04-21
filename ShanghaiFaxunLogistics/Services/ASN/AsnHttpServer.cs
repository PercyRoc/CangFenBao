using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Common.Services.Settings;
using Microsoft.Extensions.Hosting;
using Serilog;
using ShanghaiFaxunLogistics.Models.ASN;

namespace ShanghaiFaxunLogistics.Services.ASN
{
    /// <summary>
    /// ASN HTTP服务器，提供接口供WMS调用
    /// </summary>
    public class AsnHttpServer : IHostedService, IDisposable
    {
        private readonly IAsnService _asnService;
        private readonly ISettingsService _settingsService;
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private bool _isRunning;

        public AsnHttpServer(IAsnService asnService, ISettingsService settingsService)
        {
            _asnService = asnService;
            _settingsService = settingsService;
        }

        /// <summary>
        /// 启动HTTP服务
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            var settings = _settingsService.LoadSettings<AsnSettings>();
            if (!settings.IsEnabled)
            {
                Log.Information("ASN HTTP服务已禁用，不会启动监听");
                return Task.CompletedTask;
            }

            try
            {
                _cts = new CancellationTokenSource();
                _listener = new HttpListener();

                var baseUrl = settings.HttpServerUrl.TrimEnd('/');
                var appName = settings.ApplicationName.Trim('/');
                var prefix = $"{baseUrl}/{appName}/";

                Log.Information("启动ASN HTTP服务，监听地址: {Prefix}", prefix);
                _listener.Prefixes.Add(prefix);
                _listener.Start();
                _isRunning = true;

                _ = ListenAsync(_cts.Token);

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动ASN HTTP服务失败");
                throw;
            }
        }

        /// <summary>
        /// 停止HTTP服务
        /// </summary>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                _cts?.Cancel();
                _listener?.Stop();
                _isRunning = false;
                Log.Information("ASN HTTP服务已停止");

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "停止ASN HTTP服务失败");
                throw;
            }
        }

        /// <summary>
        /// 监听HTTP请求
        /// </summary>
        private async Task ListenAsync(CancellationToken cancellationToken)
        {
            while (_isRunning && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener!.GetContextAsync();

                    // 在新线程中处理请求，避免阻塞主监听线程
                    _ = Task.Run(async () => await HandleRequestAsync(context), cancellationToken);
                }
                catch (HttpListenerException ex)
                {
                    if (_isRunning)
                    {
                        Log.Error(ex, "HTTP监听异常");
                    }

                    break; // 如果已经停止，就不记录异常了
                }
                catch (OperationCanceledException)
                {
                    break; // 正常取消
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理HTTP请求时发生未预期异常");
                }
            }
        }

        /// <summary>
        /// 处理HTTP请求
        /// </summary>
        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                var urlPath = request.Url!.AbsolutePath.ToLowerInvariant();
                Log.Information("收到HTTP请求: {Method} {Path}", request.HttpMethod, urlPath);

                // 设置跨域头
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                // 处理预检请求
                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 200;
                    response.Close();
                    return;
                }

                // 只接受POST请求
                if (request.HttpMethod != "POST")
                {
                    await SendResponseAsync(response, 405, Response.CreateFailed("仅支持POST请求", "METHOD_NOT_ALLOWED"));
                    return;
                }

                // 根据路径分发请求
                if (urlPath.EndsWith("/send_asn_order_info"))
                {
                    await HandleAsnOrderInfoAsync(request, response);
                }
                else if (urlPath.EndsWith("/material_review"))
                {
                    await HandleMaterialReviewAsync(request, response);
                }
                else
                {
                    await SendResponseAsync(response, 404, Response.CreateFailed("接口不存在", "NOT_FOUND"));
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理HTTP请求时发生异常");
                try
                {
                    await SendResponseAsync(response, 500, Response.CreateFailed("服务器内部错误", "INTERNAL_ERROR"));
                }
                catch
                {
                    // 忽略发送响应时的异常
                }
            }
        }

        /// <summary>
        /// 处理ASN单数据推送请求
        /// </summary>
        private async Task HandleAsnOrderInfoAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                // 读取请求内容
                var requestBody = await ReadRequestBodyAsync(request);
                if (string.IsNullOrWhiteSpace(requestBody))
                {
                    await SendResponseAsync(response, 400, Response.CreateFailed("请求体不能为空", "INVALID_REQUEST"));
                    return;
                }

                // 反序列化请求体
                var asnInfo = JsonSerializer.Deserialize<AsnOrderInfo>(requestBody, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (asnInfo == null)
                {
                    await SendResponseAsync(response, 400, Response.CreateFailed("无效的请求数据格式", "INVALID_FORMAT"));
                    return;
                }

                // 验证必填字段
                if (string.IsNullOrWhiteSpace(asnInfo.SystemCode) ||
                    string.IsNullOrWhiteSpace(asnInfo.HouseCode) ||
                    string.IsNullOrWhiteSpace(asnInfo.OrderCode) ||
                    string.IsNullOrWhiteSpace(asnInfo.CarCode) || asnInfo.Items.Count == 0)
                {
                    await SendResponseAsync(response, 400, Response.CreateFailed("缺少必填字段", "MISSING_FIELD"));
                    return;
                }

                // 处理业务逻辑 - 同步调用
                var result = _asnService.ProcessAsnOrderInfo(asnInfo);
                
                // 返回处理结果
                await SendResponseAsync(response, 200, result);
            }
            catch (JsonException ex)
            {
                Log.Error(ex, "解析ASN单数据请求失败");
                await SendResponseAsync(response, 400, Response.CreateFailed("无效的JSON数据: " + ex.Message, "INVALID_JSON"));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理ASN单数据请求异常");
                await SendResponseAsync(response, 500, Response.CreateFailed("处理请求失败: " + ex.Message, "INTERNAL_ERROR"));
            }
        }

        /// <summary>
        /// 处理扫码复核请求
        /// </summary>
        private async Task HandleMaterialReviewAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                // 读取请求内容
                var requestBody = await ReadRequestBodyAsync(request);
                if (string.IsNullOrWhiteSpace(requestBody))
                {
                    await SendResponseAsync(response, 400, Response.CreateFailed("请求体不能为空", "INVALID_REQUEST"));
                    return;
                }

                // 反序列化请求体
                var reviewRequest = JsonSerializer.Deserialize<MaterialReviewRequest>(requestBody, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (reviewRequest == null)
                {
                    await SendResponseAsync(response, 400, Response.CreateFailed("无效的请求数据格式", "INVALID_FORMAT"));
                    return;
                }

                // 验证必填字段
                if (string.IsNullOrWhiteSpace(reviewRequest.SystemCode) ||
                    string.IsNullOrWhiteSpace(reviewRequest.HouseCode) ||
                    string.IsNullOrWhiteSpace(reviewRequest.BoxCode) ||
                    string.IsNullOrWhiteSpace(reviewRequest.ExitArea))
                {
                    await SendResponseAsync(response, 400, Response.CreateFailed("缺少必填字段", "MISSING_FIELD"));
                    return;
                }

                // 处理业务逻辑 - 同步调用
                var result = _asnService.ProcessMaterialReview(reviewRequest);
                
                // 返回处理结果
                await SendResponseAsync(response, 200, result);
            }
            catch (JsonException ex)
            {
                Log.Error(ex, "解析扫码复核请求失败");
                await SendResponseAsync(response, 400, Response.CreateFailed("无效的JSON数据: " + ex.Message, "INVALID_JSON"));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理扫码复核请求异常");
                await SendResponseAsync(response, 500, Response.CreateFailed("处理请求失败: " + ex.Message, "INTERNAL_ERROR"));
            }
        }

        /// <summary>
        /// 读取请求体内容
        /// </summary>
        private static async Task<string> ReadRequestBodyAsync(HttpListenerRequest request)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            return await reader.ReadToEndAsync();
        }

        /// <summary>
        /// 发送响应
        /// </summary>
        private static async Task SendResponseAsync<T>(HttpListenerResponse response, int statusCode, T data)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;

            await response.OutputStream.WriteAsync(buffer);
            response.Close();
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            _listener?.Close();
            _listener = null;

            GC.SuppressFinalize(this);
        }
    }
}
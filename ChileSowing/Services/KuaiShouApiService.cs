using System.Text;
using System.Xml.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using ChileSowing.Models.KuaiShou;
using ChileSowing.Models.Settings;
using Common.Services.Settings;
using Serilog;

namespace ChileSowing.Services;

/// <summary>
/// 快手API服务实现
/// </summary>
public class KuaiShouApiService(HttpClient httpClient, ISettingsService settingsService) : IKuaiShouApiService
{
    private readonly KuaiShouSettings _settings = settingsService.LoadSettings<KuaiShouSettings>(); // 在构造时加载，避免重复读取

    /// <summary>
    /// 是否启用快手接口
    /// </summary>
    public bool IsEnabled
    {
        get => _settings.IsEnabled;
    }

    /// <summary>
    /// 提交扫描信息到快手系统
    /// </summary>
    /// <param name="request">扫描信息请求</param>
    /// <returns>扫描信息响应</returns>
    public async Task<CommitScanMsgResponse?> CommitScanMsgAsync(CommitScanMsgRequest request)
    {
        if (!_settings.IsEnabled)
        {
            Log.Information("KuaiShou API is disabled");
            return null;
        }

        // 使用手动方式创建XML
        var requestXml = CreateCommitScanMsgXml(request);
        
        if (_settings.EnableDetailedLogging)
        {
            Log.Information("KuaiShou API Request: {RequestXml}", requestXml);
        }

        for (int attempt = 1; attempt <= _settings.RetryCount; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_settings.TimeoutMs));
                var content = new StringContent(requestXml, Encoding.UTF8, "application/xml");
                
                var response = await httpClient.PostAsync(_settings.ApiUrl, content, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    var responseXml = await response.Content.ReadAsStringAsync(cts.Token);
                    if (_settings.EnableDetailedLogging)
                    {
                        Log.Information("KuaiShou API Response: {ResponseXml}", responseXml);
                    }

                    // 使用手动方式解析XML
                    var result = ParseCommitScanMsgResponse(responseXml);

                    if (result == null) return result;
                    if (result.IsSuccess)
                    {
                        Log.Information("KuaiShou API success for shipId: {ShipId}, assigned chute: {Chute}",
                            request.ShipId, result.Chute);
                    }
                    else
                    {
                        Log.Warning("KuaiShou API returned error for shipId: {ShipId}, flag: {Flag}, error: {Error}",
                            request.ShipId, result.Flag, result.ErrorMessage);
                    }

                    return result;
                }
                else
                {
                    Log.Warning("KuaiShou API HTTP error: {StatusCode} - {ReasonPhrase}, attempt {Attempt}/{MaxAttempts}", 
                        response.StatusCode, response.ReasonPhrase, attempt, _settings.RetryCount);
                }
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                Log.Warning("KuaiShou API timeout on attempt {Attempt}/{MaxAttempts} for shipId: {ShipId}", 
                    attempt, _settings.RetryCount, request.ShipId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "KuaiShou API error on attempt {Attempt}/{MaxAttempts} for shipId: {ShipId}", 
                    attempt, _settings.RetryCount, request.ShipId);
            }

            if (attempt < _settings.RetryCount)
            {
                await Task.Delay(_settings.RetryDelayMs);
            }
        }

        Log.Error("KuaiShou API failed after {MaxAttempts} attempts for shipId: {ShipId}", 
            _settings.RetryCount, request.ShipId);
        return null;
    }

    /// <summary>
    /// 提交扫描信息到快手系统（简化版本）
    /// </summary>
    public async Task<CommitScanMsgResponse?> CommitScanMsgAsync(
        string shipId, 
        float weight = 0f, 
        string length = "", 
        string width = "", 
        string height = "", 
        string volume = "")
    {
        var request = new CommitScanMsgRequest
        {
            ShipId = shipId,
            Weight = weight > 0 ? weight : _settings.DefaultWeight,
            InductionId = _settings.InductionId,
            DeviceNum = _settings.DeviceNum,
            ScanPerson = _settings.ScanPerson,
            ScanType = _settings.ScanType,
            RemarkField = string.IsNullOrEmpty(_settings.RemarkField) ? "n" : _settings.RemarkField,
            Length = !string.IsNullOrEmpty(length) ? length : _settings.Length.ToString(),
            Width = !string.IsNullOrEmpty(width) ? width : _settings.Width.ToString(),
            Height = !string.IsNullOrEmpty(height) ? height : _settings.Height.ToString(),
            Volume = !string.IsNullOrEmpty(volume) ? volume : _settings.Volume.ToString(),
            ProdLine = _settings.ProdLine,
            ObjId = _settings.ObjId,
            ExpProdType = _settings.ExpProdType,
            AreaCode = _settings.AreaCode,
            EquipmentId = _settings.EquipmentId,
            PlaceCode = _settings.PlaceCode,
            RcvTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };

        return await CommitScanMsgAsync(request);
    }

    /// <summary>
    /// 测试连接
    /// </summary>
    /// <returns>是否连接成功</returns>
    public async Task<bool> TestConnectionAsync()
    {
        if (!_settings.IsEnabled)
        {
            Log.Information("KuaiShou API is disabled");
            return false;
        }

        try
        {
            // 首先检查基本的网络连通性
            var uri = new Uri(_settings.ApiUrl);
            Log.Information("Testing connection to KuaiShou API: {Host}:{Port}", uri.Host, uri.Port);

            // 基本网络连通性检查
            try
            {
                using var tcpClient = new System.Net.Sockets.TcpClient();
                using var connectCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(3000)); // 3秒连接超时
                await tcpClient.ConnectAsync(uri.Host, uri.Port, connectCts.Token);
                Log.Information("TCP connection to {Host}:{Port} successful", uri.Host, uri.Port);
            }
            catch (Exception tcpEx)
            {
                Log.Error(tcpEx, "TCP connection to {Host}:{Port} failed", uri.Host, uri.Port);
                return false;
            }

            // 创建一个测试请求
            var testRequest = new CommitScanMsgRequest
            {
                ShipId = "TEST_" + DateTime.Now.ToString("yyyyMMddHHmmss"),
                Weight = _settings.DefaultWeight,
                InductionId = _settings.InductionId,
                DeviceNum = _settings.DeviceNum,
                ScanPerson = _settings.ScanPerson,
                ScanType = _settings.ScanType,
                RemarkField = _settings.RemarkField,
                Length = _settings.Length.ToString(),
                Width = _settings.Width.ToString(),
                Height = _settings.Height.ToString(),
                Volume = _settings.Volume.ToString(),
                ProdLine = _settings.ProdLine,
                ObjId = _settings.ObjId,
                ExpProdType = _settings.ExpProdType,
                AreaCode = _settings.AreaCode,
                EquipmentId = _settings.EquipmentId,
                PlaceCode = _settings.PlaceCode,
                RcvTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            // 使用更长的超时时间进行HTTP测试
            var httpTimeoutMs = Math.Max(_settings.TimeoutMs, 10000); // 至少10秒
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(httpTimeoutMs));
            
            var requestXml = CreateCommitScanMsgXml(testRequest);
            Log.Information("Sending HTTP request to KuaiShou API with timeout: {TimeoutMs}ms", httpTimeoutMs);
            
            var content = new StringContent(requestXml, Encoding.UTF8, "application/xml");
            
            var response = await httpClient.PostAsync(_settings.ApiUrl, content, cts.Token);
            
            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                Log.Information("KuaiShou API connection test successful: {StatusCode}, Response length: {Length}", 
                    response.StatusCode, responseContent.Length);
                
                if (_settings.EnableDetailedLogging)
                {
                    Log.Information("KuaiShou API test response: {Response}", responseContent);
                }
                
                return true;
            }
            else
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                Log.Warning("KuaiShou API connection test failed: {StatusCode} - {ReasonPhrase}, Response: {Response}", 
                    response.StatusCode, response.ReasonPhrase, responseContent);
                return false;
            }
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || ex.CancellationToken.IsCancellationRequested)
        {
            Log.Error("KuaiShou API connection test timed out after {TimeoutMs}ms", 
                Math.Max(_settings.TimeoutMs, 10000));
            return false;
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "KuaiShou API HTTP request error: {Message}", ex.Message);
            return false;
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Log.Error(ex, "KuaiShou API socket error: {Message} (ErrorCode: {ErrorCode})", ex.Message, ex.ErrorCode);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "KuaiShou API connection test failed with unexpected error: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 手动创建"快手提交扫描信息接口"的请求XML
    /// </summary>
    private string CreateCommitScanMsgXml(CommitScanMsgRequest request)
    {
        // MD5加密密码
        var md5Password = GetMd5Hash(_settings.Password);

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("req", new XAttribute("op", "commitScanMsg"),
                new XElement("h",
                    // 根据文档，版本号使用最新版 V1.1.1
                    new XElement("ver", "V1.1.1"),
                    new XElement("user", _settings.User),
                    new XElement("pass", md5Password),
                    new XElement("dev", _settings.DeviceNum)
                ),
                new XElement("data",
                    new XElement("o",
                        // 严格按照文档顺序添加 <d> 元素
                        new XElement("d", request.ShipId),          // d1
                        new XElement("d", request.Weight),          // d2
                        new XElement("d", request.InductionId),     // d3
                        new XElement("d", request.DeviceNum),       // d4
                        new XElement("d", request.ScanPerson),      // d5
                        new XElement("d", request.ScanType),        // d6
                        new XElement("d", request.RemarkField),     // d7
                        new XElement("d", request.Length),          // d8 (V1.0.5+)
                        new XElement("d", request.Width),           // d9 (V1.0.5+)
                        new XElement("d", request.Height),          // d10 (V1.0.5+)
                        new XElement("d", request.Volume),          // d11 (V1.0.5+)
                        new XElement("d", request.ProdLine),        // d12 (V1.0.6+)
                        new XElement("d", request.ObjId),           // d13 (V1.0.7+)
                        new XElement("d", request.ExpProdType),     // d14 (V1.0.8+)
                        new XElement("d", request.AreaCode),        // d15 (V1.0.9+)
                        new XElement("d", request.EquipmentId),     // d16 (V1.1.0+)
                        new XElement("d", request.PlaceCode),       // d17 (V1.1.0+)
                        new XElement("d", request.RcvTime)          // d18 (V1.1.1+)
                    )
                )
            )
        );
        
        return doc.ToString();
    }

    /// <summary>
    /// 手动解析"快手提交扫描信息接口"的响应XML
    /// </summary>
    private CommitScanMsgResponse? ParseCommitScanMsgResponse(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var dta = doc.Element("dta");
            if (dta == null) return null;

            var response = new CommitScanMsgResponse
            {
                Status = dta.Attribute("st")?.Value ?? "",
                ResultCode = dta.Attribute("res")?.Value ?? ""
            };

            var dataElement = dta.Element("data");
            if (dataElement != null)
            {
                var oElement = dataElement.Element("o");
                if (oElement != null)
                {
                    var dElements = oElement.Elements("d").ToList();

                    if (response.IsSuccess) // 成功时解析
                    {
                        // 根据成功响应的文档，按顺序解析
                        if (dElements.Count > 0) response.Flag = dElements[0].Value;
                        
                        // V1.0.3+ 的返回格式
                        if (dElements.Count > 3)
                        {
                            response.AddrCode = dElements[1].Value;
                            response.Lchute = dElements[2].Value;
                            response.Chute = dElements[3].Value;
                        }
                        
                        // V1.0.5+ 的返回格式 - 备注字段
                        if (dElements.Count > 9)
                        {
                            response.ErrorMessage = dElements[9].Value;
                        }
                    }
                    else // 失败时
                    {
                        if (dElements.Count > 0)
                        {
                            // 失败时，d[0]通常是错误标识或信息
                            response.Flag = dElements[0].Value;
                            response.ErrorMessage = $"Error flag: {response.Flag}";
                        }
                    }
                }
            }
            
            return response;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to parse KuaiShou response XML: {Xml}", xml);
            return null;
        }
    }
    
    /// <summary>
    /// MD5加密方法
    /// </summary>
    private static string GetMd5Hash(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        
        using var md5 = MD5.Create();
        var inputBytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = md5.ComputeHash(inputBytes);
        return Convert.ToHexString(hashBytes).ToLower();
    }
} 
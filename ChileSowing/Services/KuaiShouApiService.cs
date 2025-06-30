using System.Text;
using System.Xml;
using System.Xml.Serialization;
using System.Net.Http;
using System.IO;
using ChileSowing.Models.KuaiShou;
using ChileSowing.Models.Settings;
using Common.Services.Settings;
using Serilog;

namespace ChileSowing.Services;

/// <summary>
/// 快手API服务实现
/// </summary>
public class KuaiShouApiService : IKuaiShouApiService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public KuaiShouApiService(HttpClient httpClient, ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
    }

    /// <summary>
    /// 是否启用快手接口
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            var settings = _settingsService.LoadSettings<KuaiShouSettings>();
            return settings.IsEnabled;
        }
    }

    /// <summary>
    /// 提交扫描信息到快手系统
    /// </summary>
    /// <param name="request">扫描信息请求</param>
    /// <returns>扫描信息响应</returns>
    public async Task<CommitScanMsgResponse?> CommitScanMsgAsync(CommitScanMsgRequest request)
    {
        var settings = _settingsService.LoadSettings<KuaiShouSettings>();
        
        if (!settings.IsEnabled)
        {
            Log.Information("KuaiShou API is disabled");
            return null;
        }

        var requestXml = SerializeToXml(request);
        if (settings.EnableDetailedLogging)
        {
            Log.Information("KuaiShou API Request: {RequestXml}", requestXml);
        }

        for (int attempt = 1; attempt <= settings.RetryCount; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(settings.TimeoutMs));
                
                var content = new StringContent(requestXml, Encoding.UTF8, "application/xml");
                var response = await _httpClient.PostAsync(settings.ApiUrl, content, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    var responseXml = await response.Content.ReadAsStringAsync();
                    if (settings.EnableDetailedLogging)
                    {
                        Log.Information("KuaiShou API Response: {ResponseXml}", responseXml);
                    }

                    var result = DeserializeFromXml<CommitScanMsgResponse>(responseXml);
                    if (result != null)
                    {
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
                    }

                    return result;
                }
                else
                {
                    Log.Warning("KuaiShou API HTTP error: {StatusCode} - {ReasonPhrase}, attempt {Attempt}/{MaxAttempts}", 
                        response.StatusCode, response.ReasonPhrase, attempt, settings.RetryCount);
                }
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                Log.Warning("KuaiShou API timeout on attempt {Attempt}/{MaxAttempts} for shipId: {ShipId}", 
                    attempt, settings.RetryCount, request.ShipId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "KuaiShou API error on attempt {Attempt}/{MaxAttempts} for shipId: {ShipId}", 
                    attempt, settings.RetryCount, request.ShipId);
            }

            if (attempt < settings.RetryCount)
            {
                await Task.Delay(settings.RetryDelayMs);
            }
        }

        Log.Error("KuaiShou API failed after {MaxAttempts} attempts for shipId: {ShipId}", 
            settings.RetryCount, request.ShipId);
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
        var settings = _settingsService.LoadSettings<KuaiShouSettings>();

        var request = new CommitScanMsgRequest
        {
            ShipId = shipId,
            Weight = weight > 0 ? weight : settings.DefaultWeight,
            InductionId = settings.InductionId,
            DeviceNum = settings.DeviceNum,
            ScanPerson = settings.ScanPerson,
            ScanType = settings.ScanType,
            RemarkField = "Scanned by ChileSowing system",
            Length = length,
            Width = width,
            Height = height,
            Volume = volume,
            ProdLine = settings.ProdLine,
            ObjId = settings.ObjId,
            ExpProdType = settings.ExpProdType,
            AreaCode = settings.AreaCode,
            EquipmentId = settings.EquipmentId,
            PlaceCode = settings.PlaceCode,
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
        var settings = _settingsService.LoadSettings<KuaiShouSettings>();
        
        if (!settings.IsEnabled)
        {
            return false;
        }

        try
        {
            // 创建一个测试请求
            var testRequest = new CommitScanMsgRequest
            {
                ShipId = "TEST_" + DateTime.Now.ToString("yyyyMMddHHmmss"),
                Weight = settings.DefaultWeight,
                InductionId = settings.InductionId,
                DeviceNum = settings.DeviceNum,
                ScanPerson = settings.ScanPerson,
                ScanType = settings.ScanType,
                RemarkField = "Connection test",
                ProdLine = settings.ProdLine,
                ObjId = settings.ObjId,
                ExpProdType = settings.ExpProdType,
                AreaCode = settings.AreaCode,
                EquipmentId = settings.EquipmentId,
                PlaceCode = settings.PlaceCode,
                RcvTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(settings.TimeoutMs));
            var requestXml = SerializeToXml(testRequest);
            var content = new StringContent(requestXml, Encoding.UTF8, "application/xml");
            
            var response = await _httpClient.PostAsync(settings.ApiUrl, content, cts.Token);
            
            Log.Information("KuaiShou API connection test result: {IsSuccess}", response.IsSuccessStatusCode);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "KuaiShou API connection test failed");
            return false;
        }
    }

    /// <summary>
    /// 序列化对象到XML字符串
    /// </summary>
    private static string SerializeToXml<T>(T obj) where T : class
    {
        try
        {
            var serializer = new XmlSerializer(typeof(T));
            using var stringWriter = new StringWriter();
            using var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings 
            { 
                Indent = true, 
                Encoding = Encoding.UTF8,
                OmitXmlDeclaration = true
            });
            
            serializer.Serialize(xmlWriter, obj);
            return stringWriter.ToString();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to serialize object to XML: {ObjectType}", typeof(T).Name);
            throw;
        }
    }

    /// <summary>
    /// 从XML字符串反序列化对象
    /// </summary>
    private static T? DeserializeFromXml<T>(string xml) where T : class
    {
        try
        {
            var serializer = new XmlSerializer(typeof(T));
            using var stringReader = new StringReader(xml);
            using var xmlReader = XmlReader.Create(stringReader);
            
            return serializer.Deserialize(xmlReader) as T;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to deserialize XML to object: {ObjectType}, XML: {Xml}", typeof(T).Name, xml);
            return null;
        }
    }
} 
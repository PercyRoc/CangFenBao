using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.IO;
using CommonLibrary.Services;
using Presentation_CommonLibrary.Services;
using Presentation_XinBa.Services.Models;
using Serilog;

namespace Presentation_XinBa.Services;

/// <summary>
/// API服务接口
/// </summary>
public interface IApiService
{
    /// <summary>
    /// 员工登录
    /// </summary>
    /// <param name="employeeId">员工ID</param>
    /// <returns>登录是否成功</returns>
    Task<bool> LoginAsync(int employeeId);
    
    /// <summary>
    /// 员工登录（带凭证）
    /// </summary>
    /// <param name="employeeId">员工ID</param>
    /// <param name="username">API用户名</param>
    /// <param name="password">API密码</param>
    /// <returns>登录是否成功</returns>
    Task<bool> LoginAsync(int employeeId, string username, string password);
    
    /// <summary>
    /// 员工登出
    /// </summary>
    /// <returns>登出是否成功</returns>
    Task<bool> LogoutAsync();
    
    /// <summary>
    /// 获取当前登录的员工ID
    /// </summary>
    /// <returns>员工ID，如果未登录则返回null</returns>
    int? GetCurrentEmployeeId();
    
    /// <summary>
    /// 检查是否已登录
    /// </summary>
    /// <returns>是否已登录</returns>
    bool IsLoggedIn();
    
    /// <summary>
    /// 提交商品尺寸
    /// </summary>
    /// <param name="goodsSticker">商品条码</param>
    /// <param name="height">高度(cm)</param>
    /// <param name="length">长度(cm)</param>
    /// <param name="width">宽度(cm)</param>
    /// <param name="weight">重量(g)</param>
    /// <param name="photoData">商品图片数据列表（可选）</param>
    /// <returns>提交是否成功</returns>
    Task<bool> SubmitDimensionsAsync(
        string goodsSticker,
        string height,
        string length,
        string width,
        string weight,
        IEnumerable<byte[]>? photoData = null);
}

/// <summary>
/// API服务实现
/// </summary>
public class ApiService : IApiService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly INotificationService _notificationService;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    
    // API认证信息
    private const string DefaultApiUsername = "test_dws";
    private const string DefaultApiPassword = "testWds";

    /// <summary>
    /// 构造函数
    /// </summary>
    public ApiService(
        HttpClient httpClient,
        ISettingsService settingsService,
        INotificationService notificationService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _notificationService = notificationService;
        
        // 设置基础URL
        var apiCredentials = _settingsService.LoadConfiguration<ApiCredentials>();
        _httpClient.BaseAddress = new Uri(apiCredentials.BaseUrl);
        
        // 设置默认基本认证
        SetBasicAuthentication(DefaultApiUsername, DefaultApiPassword);
    }
    
    /// <summary>
    /// 设置HTTP基本认证
    /// </summary>
    private void SetBasicAuthentication(string username, string password)
    {
        var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
    }
    
    /// <inheritdoc />
    public async Task<bool> LoginAsync(int employeeId)
    {
        // 使用默认凭证登录
        return await LoginAsync(employeeId, DefaultApiUsername, DefaultApiPassword);
    }
    
    /// <inheritdoc />
    public async Task<bool> LoginAsync(int employeeId, string username, string password)
    {
        try
        {
            Log.Information("尝试员工登录: EmployeeId={EmployeeId}, Username={Username}", employeeId, username);
            
            // 设置认证信息
            SetBasicAuthentication(username, password);
            
            var request = new EmployeeLoginRequest
            {
                Eid = employeeId
            };
            
            var content = new StringContent(
                JsonSerializer.Serialize(request, _jsonOptions),
                Encoding.UTF8,
                "application/json");
            
            var response = await _httpClient.PostAsync("/api/v1/employee/login", content);
            
            if (response.IsSuccessStatusCode)
            {
                // 保存登录状态
                var settings = new EmployeeSettings
                {
                    EmployeeId = employeeId,
                    IsLoggedIn = true,
                    LoginTime = DateTime.Now
                };
                
                _settingsService.SaveConfiguration(settings);
                
                // 保存API凭证
                var apiCredentials = _settingsService.LoadConfiguration<ApiCredentials>();
                apiCredentials.Username = username;
                apiCredentials.Password = password;
                _settingsService.SaveConfiguration(apiCredentials);
                
                Log.Information("员工登录成功: EmployeeId={EmployeeId}", employeeId);
                return true;
            }
            
            // 处理错误响应
            var errorContent = await response.Content.ReadAsStringAsync();
            Log.Error("员工登录失败: StatusCode={StatusCode}, Response={Response}", 
                response.StatusCode, errorContent);
            
            try
            {
                var errorResponse = JsonSerializer.Deserialize<ErrorResponse>(errorContent, _jsonOptions);
                if (errorResponse != null)
                {
                    _notificationService.ShowError($"登录失败: {errorResponse.Message}");
                }
                else
                {
                    _notificationService.ShowError($"登录失败: {response.StatusCode}");
                }
            }
            catch
            {
                _notificationService.ShowError($"登录失败: {response.StatusCode}");
            }
            
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "员工登录过程中发生错误");
            _notificationService.ShowError($"登录过程中发生错误: {ex.Message}");
            return false;
        }
    }
    
    /// <inheritdoc />
    public async Task<bool> LogoutAsync()
    {
        try
        {
            var settings = _settingsService.LoadConfiguration<EmployeeSettings>();
            if (!settings.IsLoggedIn)
            {
                Log.Warning("尝试登出但当前没有登录的员工");
                return true;
            }
            
            Log.Information("尝试员工登出: EmployeeId={EmployeeId}", settings.EmployeeId);
            
            var request = new EmployeeLoginRequest
            {
                Eid = settings.EmployeeId
            };
            
            var content = new StringContent(
                JsonSerializer.Serialize(request, _jsonOptions),
                Encoding.UTF8,
                "application/json");
            
            var response = await _httpClient.PostAsync("/api/v1/employee/logout", content);
            
            // 无论API响应如何，都清除本地登录状态
            settings.IsLoggedIn = false;
            settings.LoginTime = null;
            _settingsService.SaveConfiguration(settings);
            
            if (response.IsSuccessStatusCode)
            {
                Log.Information("员工登出成功: EmployeeId={EmployeeId}", settings.EmployeeId);
                return true;
            }
            
            // 处理错误响应
            var errorContent = await response.Content.ReadAsStringAsync();
            Log.Error("员工登出失败: StatusCode={StatusCode}, Response={Response}", 
                response.StatusCode, errorContent);
            
            try
            {
                var errorResponse = JsonSerializer.Deserialize<ErrorResponse>(errorContent, _jsonOptions);
                if (errorResponse != null)
                {
                    _notificationService.ShowError($"登出失败: {errorResponse.Message}");
                }
                else
                {
                    _notificationService.ShowError($"登出失败: {response.StatusCode}");
                }
            }
            catch
            {
                _notificationService.ShowError($"登出失败: {response.StatusCode}");
            }
            
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "员工登出过程中发生错误");
            _notificationService.ShowError($"登出过程中发生错误: {ex.Message}");
            return false;
        }
    }
    
    /// <inheritdoc />
    public int? GetCurrentEmployeeId()
    {
        var settings = _settingsService.LoadConfiguration<EmployeeSettings>();
        return settings.IsLoggedIn ? settings.EmployeeId : null;
    }
    
    /// <inheritdoc />
    public bool IsLoggedIn()
    {
        var settings = _settingsService.LoadConfiguration<EmployeeSettings>();
        return settings.IsLoggedIn;
    }
    
    /// <inheritdoc />
    public async Task<bool> SubmitDimensionsAsync(
        string goodsSticker,
        string height,
        string length,
        string width,
        string weight,
        IEnumerable<byte[]>? photoData = null)
    {
        try
        {
            Log.Information("尝试提交商品尺寸: GoodsSticker={GoodsSticker}", goodsSticker);
            
            using var formData = new MultipartFormDataContent();
            
            // 添加基本数据
            formData.Add(new StringContent(goodsSticker), "goods_sticker");
            formData.Add(new StringContent(height), "height");
            formData.Add(new StringContent(length), "length");
            formData.Add(new StringContent(width), "width");
            formData.Add(new StringContent(weight), "weight");
            
            // 添加图片数据
            if (photoData != null)
            {
                foreach (var imageBytes in photoData)
                {
                    var fileContent = new ByteArrayContent(imageBytes);
                    fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                    formData.Add(fileContent, "photos", $"image_{DateTime.Now:yyyyMMddHHmmss}.jpg");
                }
            }
            
            var response = await _httpClient.PostAsync("/api/v1/dimensions", formData);
            
            if (response.IsSuccessStatusCode)
            {
                Log.Information("商品尺寸提交成功: GoodsSticker={GoodsSticker}", goodsSticker);
                return true;
            }
            
            // 处理错误响应
            var errorContent = await response.Content.ReadAsStringAsync();
            Log.Error("商品尺寸提交失败: StatusCode={StatusCode}, Response={Response}", 
                response.StatusCode, errorContent);
            
            try
            {
                var errorResponse = JsonSerializer.Deserialize<ErrorResponse>(errorContent, _jsonOptions);
                if (errorResponse != null)
                {
                    _notificationService.ShowError($"提交失败: {errorResponse.Message}");
                }
                else
                {
                    _notificationService.ShowError($"提交失败: {response.StatusCode}");
                }
            }
            catch
            {
                _notificationService.ShowError($"提交失败: {response.StatusCode}");
            }
            
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "提交商品尺寸过程中发生错误");
            _notificationService.ShowError($"提交过程中发生错误: {ex.Message}");
            return false;
        }
    }
} 
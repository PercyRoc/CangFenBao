using System.Net.Http;
using System.Net.Http.Json;
using Common.Services.Settings;
using Serilog;
using ZtCloudWarehous.Models;
using ZtCloudWarehous.Utils;
using ZtCloudWarehous.ViewModels.Settings;

namespace ZtCloudWarehous.Services;

/// <summary>
///     称重服务实现
/// </summary>
internal class WeighingService(HttpClient httpClient, ISettingsService settingsService) : IWeighingService
{
    private const string UatBaseUrl = "https://scm-gateway-uat.ztocwst.com/edi/service/inbound/bz";
    private const string ProdBaseUrl = "https://scm-openapi.ztocwst.com/edi/service/inbound/bz";

    /// <inheritdoc />
    public async Task<WeighingResponse> SendWeightDataAsync(WeighingRequest request)
    {
        try
        {
            var settings = settingsService.LoadSettings<WeighingSettings>();
            var baseUrl = settings.IsProduction ? ProdBaseUrl : UatBaseUrl;

            // 设置公共参数
            request.CompanyCode = settings.CompanyCode;
            request.AppKey = settings.AppKey;
            request.TenantId = settings.TenantId;
            request.WarehouseCode = settings.WarehouseCode;
            request.UserRealName = settings.UserRealName;
            request.Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // 获取公共参数和业务参数
            var commonParams = SignatureHelper.GetCommonParameters(request);
            var businessParams = SignatureHelper.GetBusinessParameters(request);

            // 计算签名
            request.Sign = SignatureHelper.CalculateSignature(commonParams, businessParams, settings.Secret);

            // 发送请求
            var response = await httpClient.PostAsJsonAsync(baseUrl, request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<WeighingResponse>();
            if (result == null) throw new Exception("服务器返回空响应");

            if (!result.Success) Log.Warning("称重请求失败: Code={Code}, Message={Message}", result.Code, result.Message);

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送称重数据时发生错误");
            throw;
        }
    }
}
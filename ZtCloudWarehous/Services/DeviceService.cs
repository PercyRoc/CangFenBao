using System.Net.Http;
using System.Net.Http.Json;
using Common.Services.Settings;
using Serilog;
using ZtCloudWarehous.Models;
using ZtCloudWarehous.ViewModels.Settings;

namespace ZtCloudWarehous.Services;

/// <summary>
///     设备服务实现
/// </summary>
public class DeviceService : IDeviceService
{
    private readonly Timer _businessDataTimer;
    private readonly Timer _heartbeatTimer;
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private bool _isRunning;
    private WeighingSettings? _settings;

    public DeviceService(HttpClient httpClient, ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;

        // 创建心跳定时器（5分钟）
        _heartbeatTimer = new Timer(state => { _ = SendHeartbeatAsync(); }, null, Timeout.Infinite, Timeout.Infinite);

        // 创建业务数据同步定时器（10分钟）
        _businessDataTimer = new Timer(state => { _ = SyncBusinessDataInternalAsync(); }, null, Timeout.Infinite,
            Timeout.Infinite);
    }

    /// <inheritdoc />
    public async Task<DeviceResponse> RegisterAsync(DeviceRegisterRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/device/register", request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<DeviceResponse>();
            return result ?? new DeviceResponse
            {
                Code = 1,
                Msg = "注册失败：响应为空"
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设备注册失败");
            return new DeviceResponse
            {
                Code = 1,
                Msg = $"注册失败：{ex.Message}"
            };
        }
    }

    /// <inheritdoc />
    public async Task<DeviceResponse> OnlineNotifyAsync(DeviceBaseRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/device/online", request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<DeviceResponse>();
            return result ?? new DeviceResponse
            {
                Code = 1,
                Msg = "上线通知失败：响应为空"
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设备上线通知失败");
            return new DeviceResponse
            {
                Code = 1,
                Msg = $"上线通知失败：{ex.Message}"
            };
        }
    }

    /// <inheritdoc />
    public async Task<DeviceResponse> OfflineNotifyAsync(DeviceBaseRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/device/offline", request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<DeviceResponse>();
            return result ?? new DeviceResponse
            {
                Code = 1,
                Msg = "下线通知失败：响应为空"
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设备下线通知失败");
            return new DeviceResponse
            {
                Code = 1,
                Msg = $"下线通知失败：{ex.Message}"
            };
        }
    }

    /// <inheritdoc />
    public async Task<DeviceResponse> HeartbeatAsync(DeviceBaseRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/device/heartbeat", request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<DeviceResponse>();
            return result ?? new DeviceResponse
            {
                Code = 1,
                Msg = "心跳通知失败：响应为空"
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设备心跳通知失败");
            return new DeviceResponse
            {
                Code = 1,
                Msg = $"心跳通知失败：{ex.Message}"
            };
        }
    }

    /// <inheritdoc />
    public async Task<DeviceResponse> SyncActionDataAsync(DeviceActionRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/device/action", request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<DeviceResponse>();
            return result ?? new DeviceResponse
            {
                Code = 1,
                Msg = "动作数据同步失败：响应为空"
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设备动作数据同步失败");
            return new DeviceResponse
            {
                Code = 1,
                Msg = $"动作数据同步失败：{ex.Message}"
            };
        }
    }

    /// <inheritdoc />
    public async Task<DeviceResponse> SyncBusinessDataAsync(BusinessDataRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/device/business", request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<DeviceResponse>();
            return result ?? new DeviceResponse
            {
                Code = 1,
                Msg = "业务数据同步失败：响应为空"
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "业务数据同步失败");
            return new DeviceResponse
            {
                Code = 1,
                Msg = $"业务数据同步失败：{ex.Message}"
            };
        }
    }

    /// <inheritdoc />
    public async Task StartAsync()
    {
        if (_isRunning) return;

        try
        {
            _settings = _settingsService.LoadSettings<WeighingSettings>();

            // 注册设备
            var registerRequest = new DeviceRegisterRequest
            {
                WareHouseCode = _settings.WarehouseCode,
                EquipmentCode = _settings.EquipmentCode,
                EquipmentType = "WeighingDevice",
                Manufacturer = "Default"
            };

            var registerResponse = await RegisterAsync(registerRequest);
            if (registerResponse.Code != 0)
            {
                Log.Error("设备注册失败：{Message}", registerResponse.Msg);
                return;
            }

            // 发送上线通知
            var onlineRequest = new DeviceBaseRequest
            {
                WareHouseCode = _settings.WarehouseCode,
                EquipmentCode = _settings.EquipmentCode
            };

            var onlineResponse = await OnlineNotifyAsync(onlineRequest);
            if (onlineResponse.Code != 0)
            {
                Log.Error("设备上线通知失败：{Message}", onlineResponse.Msg);
                return;
            }

            // 启动心跳定时器（5分钟）
            _heartbeatTimer.Change(TimeSpan.Zero, TimeSpan.FromMinutes(5));

            // 启动业务数据同步定时器（10分钟）
            _businessDataTimer.Change(TimeSpan.Zero, TimeSpan.FromMinutes(10));

            _isRunning = true;
            Log.Information("设备服务已启动");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动设备服务失败");
        }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (!_isRunning) return;

        try
        {
            // 停止定时器
            _heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _businessDataTimer.Change(Timeout.Infinite, Timeout.Infinite);

            // 发送下线通知
            if (_settings != null)
            {
                var offlineRequest = new DeviceBaseRequest
                {
                    WareHouseCode = _settings.WarehouseCode,
                    EquipmentCode = _settings.EquipmentCode
                };

                await OfflineNotifyAsync(offlineRequest);
            }

            _isRunning = false;
            Log.Information("设备服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止设备服务失败");
        }
    }

    private async Task SendHeartbeatAsync()
    {
        try
        {
            if (_settings == null) return;

            var request = new DeviceBaseRequest
            {
                WareHouseCode = _settings.WarehouseCode,
                EquipmentCode = _settings.EquipmentCode
            };

            await HeartbeatAsync(request);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送心跳失败");
        }
    }

    private async Task SyncBusinessDataInternalAsync()
    {
        try
        {
            if (_settings == null) return;

            var request = new BusinessDataRequest
            {
                WareHouseCode = _settings.WarehouseCode,
                EquipmentCode = _settings.EquipmentCode,
                Data = new BusinessData
                {
                    Total = 0, // TODO: 从实际业务中获取数据
                    SuccessQty = 0,
                    FailQty = 0
                }
            };

            await SyncBusinessDataAsync(request);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "同步业务数据失败");
        }
    }
}
using Common.Services;
using Common.Services.Settings;
using Microsoft.Extensions.Hosting;
using Presentation_SeedingWall.Models;
using Serilog;

namespace Presentation_SeedingWall.Services;

/// <summary>
///     聚水潭启动服务，用于在程序启动时自动启动聚水潭服务
/// </summary>
public class JuShuiTanStartupService : IHostedService
{
    private readonly IJuShuiTanService _juShuiTanService;
    private readonly ISettingsService _settingsService;
    private bool _isStarted;
    private JuShuiTanSettings? _settings;

    /// <summary>
    ///     构造函数
    /// </summary>
    /// <param name="juShuiTanService">聚水潭服务</param>
    /// <param name="settingsService">设置服务</param>
    public JuShuiTanStartupService(
        IJuShuiTanService juShuiTanService,
        ISettingsService settingsService)
    {
        _juShuiTanService = juShuiTanService;
        _settingsService = settingsService;
    }

    /// <summary>
    ///     启动服务
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_isStarted)
        {
            Log.Warning("聚水潭服务已经启动，无需重复启动");
            return;
        }

        try
        {
            Log.Information("正在启动聚水潭服务...");

            // 加载设置
            _settings = _settingsService.LoadSettings<JuShuiTanSettings>();
            if (_settings == null)
            {
                Log.Warning("未找到聚水潭设置，使用默认设置");
                _settings = new JuShuiTanSettings();
            }

            Log.Information("正在连接到聚水潭服务器: {ServerUrl}", _settings.ServerUrl);
            var result = await _juShuiTanService.ConnectAsync(_settings.ServerUrl);
            if (result)
                Log.Information("已成功连接到聚水潭服务器");
            else
                Log.Warning("连接到聚水潭服务器失败");

            _isStarted = true;
            Log.Information("聚水潭服务启动完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动聚水潭服务时发生错误");
            throw;
        }
    }

    /// <summary>
    ///     停止服务
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_isStarted)
        {
            Log.Warning("聚水潭服务尚未启动，无需停止");
            return Task.CompletedTask;
        }

        try
        {
            Log.Information("正在停止聚水潭服务...");

            // 断开连接
            _juShuiTanService.Disconnect();

            _isStarted = false;
            Log.Information("聚水潭服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止聚水潭服务时发生错误");
        }

        return Task.CompletedTask;
    }
}
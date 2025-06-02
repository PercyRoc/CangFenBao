// using Common.Services.Settings;
// using Serilog;
// using ShanghaiModuleBelt.Models;
//
// namespace ShanghaiModuleBelt.Services;
//
// /// <summary>
// ///     锁格服务托管服务，负责管理锁格服务的生命周期
// /// </summary>
// /// <remarks>
// ///     初始化锁格服务托管服务
// /// </remarks>
// /// <param name="settingsService">设置服务</param>
// internal class LockingHostedService(ISettingsService settingsService) : IDisposable
// {
//     private readonly CancellationTokenSource _cts = new();
//     private readonly ISettingsService _settingsService = settingsService;
//     private bool _disposed;
//     private bool _isRunning;
//
//     /// <summary>
//     ///     释放资源
//     /// </summary>
//     public void Dispose()
//     {
//         if (_disposed) return;
//
//         try
//         {
//             _cts.Cancel();
//             _cts.Dispose();
//         }
//         catch (Exception ex)
//         {
//             Log.Error(ex, "释放锁格服务托管服务资源时出错");
//         }
//
//         _disposed = true;
//
//         GC.SuppressFinalize(this);
//     }
//
//     /// <summary>
//     ///     启动服务
//     /// </summary>
//     internal async Task StartAsync()
//     {
//         if (_isRunning) return;
//
//         try
//         {
//             Log.Information("正在启动锁格服务托管服务...");
//
//             // 加载设置
//             var settings = _settingsService.LoadSettings<TcpSettings>();
//             Log.Information("锁格服务配置: {Address}:{Port}", settings.Address, settings.Port);
//
//             // 标记为运行中
//             _isRunning = true;
//
//             Log.Information("锁格服务托管服务已启动");
//
//             await Task.CompletedTask;
//         }
//         catch (Exception ex)
//         {
//             Log.Error(ex, "启动锁格服务托管服务时出错");
//             throw;
//         }
//     }
//
//     /// <summary>
//     ///     停止服务
//     /// </summary>
//     internal async Task StopAsync()
//     {
//         if (!_isRunning) return;
//
//         try
//         {
//             Log.Information("正在停止锁格服务托管服务...");
//
//             // 取消后台任务
//             await _cts.CancelAsync();
//
//             // 标记为已停止
//             _isRunning = false;
//
//             Log.Information("锁格服务托管服务已停止");
//
//             await Task.CompletedTask;
//         }
//         catch (Exception ex)
//         {
//             Log.Error(ex, "停止锁格服务托管服务时出错");
//             throw;
//         }
//     }
// }
using System.Text.Json;
using System.Timers;
using Microsoft.EntityFrameworkCore;
using Modules.Models.Jitu;
using Modules.Services.Jitu;
using Serilog;
using ShanghaiModuleBelt.Data;
using ShanghaiModuleBelt.Models;
using ShanghaiModuleBelt.Models.Sto;
using ShanghaiModuleBelt.Models.Yunda;
using ShanghaiModuleBelt.Models.Zto;
using ShanghaiModuleBelt.Services.Sto;
using ShanghaiModuleBelt.Services.Yunda;
using ShanghaiModuleBelt.Services.Zto;

namespace ShanghaiModuleBelt.Services;

public class RetryService : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IStoAutoReceiveService _stoService;
    private readonly IYundaUploadWeightService _yundaService;
    private readonly IZtoApiService _ztoService;
    private readonly IJituService _jituService;
    private readonly System.Timers.Timer _retryTimer;
    private readonly System.Timers.Timer _cleanupTimer;
    private bool _disposed;
    private const int MaxRetries = 3;
    private const int RetryIntervalMinutes = 5;
    private const int CleanupDays = 30;

    public RetryService(
        ApplicationDbContext dbContext,
        IStoAutoReceiveService stoService,
        IYundaUploadWeightService yundaService,
        IZtoApiService ztoService,
        IJituService jituService)
    {
        _dbContext = dbContext;
        _stoService = stoService;
        _yundaService = yundaService;
        _ztoService = ztoService;
        _jituService = jituService;

        // 设置每天凌晨2点执行重传
        var now = DateTime.Now;
        var nextRun = now.Date.AddDays(1).AddHours(2);
        var timeToNextRun = nextRun - now;

        _retryTimer = new System.Timers.Timer(timeToNextRun.TotalMilliseconds);
        _retryTimer.Elapsed += RetryTimer_Elapsed;
        _retryTimer.Start();

        // 设置每天凌晨3点执行清理
        var nextCleanup = now.Date.AddDays(1).AddHours(3);
        var timeToNextCleanup = nextCleanup - now;

        _cleanupTimer = new System.Timers.Timer(timeToNextCleanup.TotalMilliseconds);
        _cleanupTimer.Elapsed += CleanupTimer_Elapsed;
        _cleanupTimer.Start();

        Log.Information("重传服务已启动，将在 {NextRun} 执行首次重传", nextRun);
        Log.Information("清理服务已启动，将在 {NextCleanup} 执行首次清理", nextCleanup);
    }

    public async Task AddRetryRecordAsync(string barcode, string company, object requestData, string? errorMessage = null)
    {
        try
        {
            var record = new RetryRecord
            {
                Barcode = barcode,
                Company = company,
                RequestData = JsonSerializer.Serialize(requestData),
                CreateTime = DateTime.Now,
                ErrorMessage = errorMessage,
                RetryCount = 0
            };

            await _dbContext.RetryRecords.AddAsync(record);
            await _dbContext.SaveChangesAsync();
            Log.Information("已添加重传记录: {Barcode}, {Company}", barcode, company);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "添加重传记录失败: {Barcode}, {Company}", barcode, company);
        }
    }

    private async void RetryTimer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        try
        {
            Log.Information("开始执行每日重传任务");
            
            // 获取今天未重传的记录
            var today = DateTime.Today;
            var records = await _dbContext.RetryRecords
                .Where(r => r.CreateTime.Date == today && !r.IsRetried && r.RetryCount < MaxRetries)
                .ToListAsync();

            if (records.Count == 0)
            {
                Log.Information("今日没有需要重传的记录");
                return;
            }

            Log.Information("找到 {Count} 条需要重传的记录", records.Count);

            foreach (var record in records)
            {
                try
                {
                    bool success = false;
                    switch (record.Company)
                    {
                        case "申通":
                            var stoRequest = JsonSerializer.Deserialize<StoAutoReceiveRequest>(record.RequestData);
                            if (stoRequest != null)
                            {
                                var response = await _stoService.SendAutoReceiveRequestAsync(stoRequest);
                                success = response is { Success: true };
                            }
                            break;

                        case "韵达":
                            var yundaRequest = JsonSerializer.Deserialize<YundaUploadWeightRequest>(record.RequestData);
                            if (yundaRequest != null)
                            {
                                var response = await _yundaService.SendUploadWeightRequestAsync(yundaRequest);
                                success = response is { Result: true, Code: "0000" };
                            }
                            break;

                        case "中通":
                            var ztoRequest = JsonSerializer.Deserialize<CollectUploadRequest>(record.RequestData);
                            if (ztoRequest != null)
                            {
                                var response = await _ztoService.UploadCollectTraceAsync(ztoRequest);
                                success = response is { Status: true };
                            }
                            break;

                        case "极兔":
                            var jituRequest = JsonSerializer.Deserialize<JituOpScanRequest>(record.RequestData);
                            if (jituRequest != null)
                            {
                                var response = await _jituService.SendOpScanRequestAsync(jituRequest);
                                success = response is { Success: true, Code: 200 };
                            }
                            break;
                    }

                    record.RetryCount++;
                    record.LastRetryTime = DateTime.Now;

                    if (success)
                    {
                        record.IsRetried = true;
                        record.RetryTime = DateTime.Now;
                        Log.Information("包裹 {Barcode} 重传成功", record.Barcode);
                    }
                    else
                    {
                        if (record.RetryCount >= MaxRetries)
                        {
                            record.IsRetried = true;
                            record.ErrorMessage = $"重传失败，已达到最大重试次数 {MaxRetries}";
                            Log.Warning("包裹 {Barcode} 重传失败，已达到最大重试次数", record.Barcode);
                        }
                        else
                        {
                            record.ErrorMessage = "重传失败，等待下次重试";
                            Log.Warning("包裹 {Barcode} 重传失败，将在 {Interval} 分钟后重试", 
                                record.Barcode, RetryIntervalMinutes);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "重传包裹失败: {Barcode}, {Company}", record.Barcode, record.Company);
                    record.ErrorMessage = ex.Message;
                    record.RetryCount++;
                    
                    if (record.RetryCount >= MaxRetries)
                    {
                        record.IsRetried = true;
                    }
                }
            }

            await _dbContext.SaveChangesAsync();
            Log.Information("每日重传任务完成，共处理 {Count} 条记录", records.Count);

            // 设置下一次执行时间（24小时后）
            _retryTimer.Interval = TimeSpan.FromHours(24).TotalMilliseconds;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行每日重传任务时发生错误");
        }
    }

    private async void CleanupTimer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        try
        {
            Log.Information("开始执行清理任务");
            
            var cutoffDate = DateTime.Today.AddDays(-CleanupDays);
            var recordsToDelete = await _dbContext.RetryRecords
                .Where(r => r.CreateTime < cutoffDate)
                .ToListAsync();

            if (recordsToDelete.Any())
            {
                _dbContext.RetryRecords.RemoveRange(recordsToDelete);
                await _dbContext.SaveChangesAsync();
                Log.Information("已清理 {Count} 条过期记录", recordsToDelete.Count);
            }
            else
            {
                Log.Information("没有需要清理的过期记录");
            }

            // 设置下一次执行时间（24小时后）
            _cleanupTimer.Interval = TimeSpan.FromHours(24).TotalMilliseconds;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行清理任务时发生错误");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _retryTimer.Stop();
        _retryTimer.Dispose();
        
        _cleanupTimer.Stop();
        _cleanupTimer.Dispose();
        
        _disposed = true;
    }
}
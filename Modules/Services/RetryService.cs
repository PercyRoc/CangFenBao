using System.Text.Json;
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
    private bool _disposed;
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
                IsRetried = false
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

    public async Task PerformRetryAsync()
    {
        try
        {
            Log.Information("开始执行手动重传任务：重传所有没有重传过的记录");

            var records = await _dbContext.RetryRecords
                .Where(r => !r.IsRetried)
                .ToListAsync();

            if (records.Count == 0)
            {
                Log.Information("没有需要重传的记录");
                return;
            }

            Log.Information("找到 {Count} 条需要重传的记录", records.Count);

            foreach (var record in records)
            {
                try
                {
                    record.ErrorMessage = null;

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

                    record.IsRetried = true;
                    record.RetryTime = DateTime.Now;

                    if (success)
                    {
                        Log.Information("包裹 {Barcode} 重传成功", record.Barcode);
                    }
                    else
                    {
                        record.ErrorMessage = record.ErrorMessage ?? "重传失败";
                        Log.Warning("包裹 {Barcode} 重传失败，已标记为已重传", record.Barcode);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "重传包裹失败: {Barcode}, {Company}", record.Barcode, record.Company);
                    record.ErrorMessage = ex.Message;
                    record.IsRetried = true;
                }
            }

            await _dbContext.SaveChangesAsync();
            Log.Information("手动重传任务完成，共处理 {Count} 条记录", records.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行手动重传任务时发生错误");
        }
    }

    public async Task CleanupExpiredRecordsAsync()
    {
        try
        {
            Log.Information("开始执行清理过期重传记录任务");
            
            var cutoffDate = DateTime.Today.AddDays(-CleanupDays);
            var recordsToDelete = await _dbContext.RetryRecords
                .Where(r => r.CreateTime < cutoffDate && r.IsRetried)
                .ToListAsync();

            if (recordsToDelete.Any())
            {
                _dbContext.RetryRecords.RemoveRange(recordsToDelete);
                await _dbContext.SaveChangesAsync();
                Log.Information("已清理 {Count} 条过期且已重传的记录", recordsToDelete.Count);
            }
            else
            {
                Log.Information("没有需要清理的过期记录");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "执行清理过期记录任务时发生错误");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
    }
}
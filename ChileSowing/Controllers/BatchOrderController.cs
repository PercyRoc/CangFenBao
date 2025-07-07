using ChileSowing.Models.Api;
using ChileSowing.Models.Settings;
using Common.Services.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ChileSowing.Controllers;

/// <summary>
/// 分拣单数据同步API控制器
/// </summary>
[Route("chile-sowing")]
public class BatchOrderController : Controller
{
    private readonly ISettingsService _settingsService;
    private readonly ILogger<BatchOrderController> _logger;

    public BatchOrderController(ISettingsService settingsService, ILogger<BatchOrderController> logger)
    {
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>
    /// 分拣单数据同步接口
    /// </summary>
    /// <param name="request">分拣单数据</param>
    /// <returns>同步结果</returns>
    [HttpPost("send_batch_order_info")]
    public async Task<IActionResult> send_batch_order_info([FromBody] BatchOrderRequest request)
    {
        try
        {
            _logger.LogInformation("收到分拣单数据同步请求: OrderNo={OrderNo}, Items={ItemCount}", 
                request.OrderNo, request.Items?.Count ?? 0);

            // 验证请求数据
            if (string.IsNullOrEmpty(request.SystemCode))
            {
                _logger.LogWarning("系统编码不能为空");
                return Ok(BatchOrderResponse.CreateFailure("INVALID_SYSTEM_CODE", "系统编码不能为空"));
            }

            if (string.IsNullOrEmpty(request.HouseCode))
            {
                _logger.LogWarning("仓库编码不能为空");
                return Ok(BatchOrderResponse.CreateFailure("INVALID_HOUSE_CODE", "仓库编码不能为空"));
            }

            if (string.IsNullOrEmpty(request.OrderNo))
            {
                _logger.LogWarning("分拣单号不能为空");
                return Ok(BatchOrderResponse.CreateFailure("INVALID_ORDER_NO", "分拣单号不能为空"));
            }

            if (request.Items == null || !request.Items.Any())
            {
                _logger.LogWarning("订单明细不能为空");
                return Ok(BatchOrderResponse.CreateFailure("INVALID_ITEMS", "订单明细不能为空"));
            }

            // 验证订单明细
            for (int i = 0; i < request.Items.Count; i++)
            {
                var item = request.Items[i];
                if (string.IsNullOrEmpty(item.DetailCode))
                {
                    _logger.LogWarning("订单明细[{Index}]的明细号不能为空", i);
                    return Ok(BatchOrderResponse.CreateFailure("INVALID_DETAIL_CODE", $"订单明细[{i}]的明细号不能为空"));
                }

                if (string.IsNullOrEmpty(item.ItemCode))
                {
                    _logger.LogWarning("订单明细[{Index}]的物料条码不能为空", i);
                    return Ok(BatchOrderResponse.CreateFailure("INVALID_ITEM_CODE", $"订单明细[{i}]的物料条码不能为空"));
                }

                if (string.IsNullOrEmpty(item.ShopCode))
                {
                    _logger.LogWarning("订单明细[{Index}]的门店代码不能为空", i);
                    return Ok(BatchOrderResponse.CreateFailure("INVALID_SHOP_CODE", $"订单明细[{i}]的门店代码不能为空"));
                }
            }

            // 处理分拣单数据
            await ProcessBatchOrderAsync(request);

            _logger.LogInformation("分拣单数据同步成功: OrderNo={OrderNo}", request.OrderNo);
            return Ok(BatchOrderResponse.CreateSuccess("分拣单数据同步成功", new { OrderNo = request.OrderNo }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理分拣单数据同步请求时发生异常");
            return Ok(BatchOrderResponse.CreateFailure("INTERNAL_ERROR", "服务器内部错误"));
        }
    }

    /// <summary>
    /// 处理分拣单数据
    /// </summary>
    /// <param name="request">分拣单请求</param>
    private async Task ProcessBatchOrderAsync(BatchOrderRequest request)
    {
        // TODO: 在这里实现具体的分拣单处理逻辑
        // 例如：
        // 1. 存储分拣单信息到数据库
        // 2. 通知播种分拣系统
        // 3. 更新相关状态
        // 4. 发送事件通知

        _logger.LogInformation("开始处理分拣单: {OrderNo}", request.OrderNo);
        
        // 模拟异步处理
        await Task.Delay(100);
        
        // 记录订单明细
        foreach (var item in request.Items)
        {
            _logger.LogDebug("处理物料: DetailCode={DetailCode}, ItemCode={ItemCode}, ShopCode={ShopCode}", 
                item.DetailCode, item.ItemCode, item.ShopCode);
        }

        _logger.LogInformation("分拣单处理完成: {OrderNo}", request.OrderNo);
    }
} 
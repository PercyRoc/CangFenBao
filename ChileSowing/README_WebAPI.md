# 智利播种墙 Web API 服务器

## 概述

智利播种墙现已集成Web API服务器功能，用于接收来自WMS系统的分拣单数据同步请求。服务器使用ASP.NET Core Kestrel构建，支持高性能的HTTP请求处理。

## 功能特性

- **分拣单数据同步**：接收WMS系统推送的分拣单数据
- **数据验证**：完整的请求数据验证和错误处理
- **标准化响应**：统一的API响应格式
- **日志记录**：详细的操作日志记录
- **配置管理**：支持端口、主机等配置项

## 配置说明

### appsettings.json 配置

```json
{
  "WebServerSettings": {
    "IsEnabled": true,          // 是否启用Web服务器
    "Port": 8080,               // 服务器端口
    "Host": "*",                // 监听主机地址（*表示所有地址）
    "AppName": "chile-sowing"   // 应用名称（用于URL路径）
  }
}
```

### 服务器地址

- **默认地址**：`http://localhost:8080`
- **API基础路径**：`/chile-sowing`
- **完整接口地址**：`http://localhost:8080/chile-sowing/send_batch_order_info`

## API 接口说明

### 分拣单数据同步接口

**接口地址**：`POST /chile-sowing/send_batch_order_info`

**请求格式**：`Content-Type: application/json`

**请求参数**：

| 字段名 | 类型 | 必选 | 说明 |
|--------|------|------|------|
| systemCode | string | 是 | 系统编码 |
| houseCode | string | 是 | 仓库编码 |
| orderNo | string | 是 | 分拣单号 |
| priority | integer | 否 | 执行优先级（默认40，范围1-100） |
| remark | string | 否 | 备注 |
| items | array | 是 | 订单明细数组 |
| extra | object | 否 | 扩展项 |

**订单明细（items）参数**：

| 字段名 | 类型 | 必选 | 说明 |
|--------|------|------|------|
| detailCode | string | 是 | 订单明细号 |
| itemCode | string | 是 | 物料条码 |
| skuCode | string | 否 | SKU代码 |
| skuName | string | 否 | SKU名称 |
| shopCode | string | 是 | 门店代码 |
| shopName | string | 否 | 门店名称 |

**响应格式**：

```json
{
  "success": true,
  "code": "SUCCESS",
  "message": "分拣单数据同步成功",
  "time": "2024-12-15 10:30:00",
  "object": {
    "orderNo": "SO20241215001"
  }
}
```

## 使用示例

### 使用 curl 测试

```bash
curl -X POST http://localhost:8080/chile-sowing/send_batch_order_info \
  -H "Content-Type: application/json" \
  -d @api_test_example.json
```

### 使用 PowerShell 测试

```powershell
$body = Get-Content -Path "api_test_example.json" -Raw
$headers = @{
    "Content-Type" = "application/json"
}

Invoke-RestMethod -Uri "http://localhost:8080/chile-sowing/send_batch_order_info" `
                  -Method POST `
                  -Body $body `
                  -Headers $headers
```

### 使用 C# HttpClient 测试

```csharp
using System.Text;
using System.Text.Json;

var client = new HttpClient();
var request = new
{
    systemCode = "WMS001",
    houseCode = "WH001",
    orderNo = "SO20241215001",
    priority = 50,
    remark = "测试分拣单",
    items = new[]
    {
        new
        {
            detailCode = "D001",
            itemCode = "BC123456789",
            skuCode = "SKU001",
            skuName = "测试商品1",
            shopCode = "SHOP001",
            shopName = "测试门店1"
        }
    }
};

var json = JsonSerializer.Serialize(request);
var content = new StringContent(json, Encoding.UTF8, "application/json");

var response = await client.PostAsync("http://localhost:8080/chile-sowing/send_batch_order_info", content);
var result = await response.Content.ReadAsStringAsync();
Console.WriteLine(result);
```

## 错误码说明

| 错误码 | 说明 |
|--------|------|
| SUCCESS | 成功 |
| INVALID_SYSTEM_CODE | 系统编码不能为空 |
| INVALID_HOUSE_CODE | 仓库编码不能为空 |
| INVALID_ORDER_NO | 分拣单号不能为空 |
| INVALID_ITEMS | 订单明细不能为空 |
| INVALID_DETAIL_CODE | 订单明细号不能为空 |
| INVALID_ITEM_CODE | 物料条码不能为空 |
| INVALID_SHOP_CODE | 门店代码不能为空 |
| INTERNAL_ERROR | 服务器内部错误 |

## 日志记录

所有API请求和响应都会记录到日志文件中：

- **日志路径**：`logs/ChileSowing-{日期}.log`
- **日志级别**：包含请求接收、数据验证、处理过程、错误信息等

## 注意事项

1. **端口冲突**：如果8080端口被占用，请修改appsettings.json中的Port配置
2. **防火墙设置**：确保服务器端口在防火墙中开放
3. **数据验证**：所有必选字段都会进行验证，请确保请求数据完整
4. **字符编码**：请求和响应都使用UTF-8编码
5. **超时设置**：默认没有超时限制，如需要可在WMS客户端设置

## 扩展开发

如需要扩展更多API接口，请参考以下步骤：

1. 在 `Models/Api/` 目录下创建新的请求/响应模型
2. 在 `Controllers/` 目录下创建新的控制器或扩展现有控制器
3. 在 `Services/WebServerService.cs` 中注册新的控制器
4. 更新相关配置和文档

## 技术支持

如有问题，请查看日志文件或联系技术支持团队。 
# Tare Attributes API 集成说明

## 概述

欣巴模块现已集成 Wildberries Tare Attributes API，用于向 Wildberries 系统提交包裹测量属性（尺寸、重量等）。

## 新增功能

### 1. API 服务
- **ITareAttributesApiService**: 服务接口
- **TareAttributesApiService**: 服务实现
- 支持自动重试和错误处理
- 完整的日志记录

### 2. 数据模型
- **TareAttributesRequest**: 请求数据模型
- **TareAttributesErrorResponse**: 错误响应模型
- 自动数据验证和体积计算

### 3. 集成方式
- 在 `MainWindowViewModel` 中自动调用
- 与现有的 Dimensions Machine API 并行提交
- 包裹数据自动单位转换

## API 规范

### 请求地址
```
POST https://wh-skud-external.wildberries.ru/srv/measure_machine_api/api/tare_attributes_from_machine
```

### 认证方式
- **类型**: HTTP Basic Authentication
- **用户名**: yaoli
- **密码**: L4T97kdYBKHg1YTkSmccy3YvnSibr4z66NtpxJ28buSjaXdIKEMJvbY8bqewbkIi

### 请求数据格式
```json
{
    "office_id": 300684,
    "tare_sticker": "包裹条码",
    "place_id": 943626653,
    "size_a_mm": 长度(毫米),
    "size_b_mm": 宽度(毫米),
    "size_c_mm": 高度(毫米),
    "volume_mm": 体积(立方毫米),
    "weight_g": 重量(克)
}
```

### 响应状态
- **204 No Content**: 提交成功
- **400 Bad Request**: 请求格式错误
- **401 Unauthorized**: 认证失败
- **422 Unprocessable Entity**: 业务逻辑错误
- **500 Internal Server Error**: 服务器错误

## 使用示例

### 基本用法
```csharp
// 注入服务
private readonly ITareAttributesApiService _tareAttributesApiService;

// 创建请求
var request = new TareAttributesRequest
{
    TareSticker = "包裹条码",
    SizeAMm = 200,      // 长度 200mm
    SizeBMm = 150,      // 宽度 150mm  
    SizeCMm = 100,      // 高度 100mm
    WeightG = 500       // 重量 500g
    // OfficeId 和 PlaceId 使用默认硬编码值
    // VolumeMm 将自动计算
};

// 提交数据
var (success, errorMessage) = await _tareAttributesApiService.SubmitTareAttributesAsync(request);

if (success)
{
    Log.Information("提交成功");
}
else
{
    Log.Warning("提交失败: {Error}", errorMessage);
}
```

### 自动集成
在 `MainWindowViewModel.OnPackageReceived` 方法中，系统会自动：

1. 检查包裹是否有完整的尺寸和重量数据
2. 将数据从厘米/千克转换为毫米/克
3. 同时提交到两个 API：
   - Dimensions Machine API（毫米/毫克）
   - Tare Attributes API（毫米/克）
4. 记录详细的提交日志

## 配置说明

### 硬编码值
- **office_id**: 300684 (Shelepanovo 仓库)
- **place_id**: 943626653 (Shelepanovo 机器)

### 数据转换
- **长度/宽度/高度**: 厘米 × 10 = 毫米
- **重量**: 千克 × 1000 = 克
- **体积**: 长 × 宽 × 高 (立方毫米)

## 错误处理

### 常见错误
1. **网络连接问题**: 自动记录并显示用户友好的错误信息
2. **认证失败**: 检查用户名和密码配置
3. **数据验证失败**: 确保所有必填字段都有有效值
4. **服务器错误**: 查看详细日志进行故障排查

### 日志级别
- **Information**: 成功提交和重要状态变化
- **Warning**: 提交失败和数据问题
- **Error**: 网络错误和异常情况
- **Debug**: 详细的请求和响应数据

## 注意事项

1. **数据完整性**: 确保包裹有完整的尺寸和重量数据
2. **单位转换**: 系统自动处理单位转换，无需手动计算
3. **并行提交**: 两个 API 独立提交，一个失败不影响另一个
4. **错误恢复**: 网络错误会自动重试，业务错误需要检查数据

## 监控和调试

### 关键日志
```
// 成功提交
Tare Attributes API 提交成功: Barcode={Barcode}, Size={Length}x{Width}x{Height}mm, Weight={Weight}g

// 提交失败  
Tare Attributes API 提交失败: Barcode={Barcode}, Error={Error}

// 数据不完整
包裹缺少必要的尺寸或重量信息，无法提交到API: Barcode={Barcode}
```

### 性能监控
- 请求响应时间
- 成功率统计
- 错误类型分布
- 网络连接状态 
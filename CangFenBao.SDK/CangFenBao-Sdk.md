## **CangFenBao.SDK 详细文档**

### **1. 概述**

`CangFenBao.SDK` 是一个专为物流分拣和包裹处理系统设计的 .NET 库。它高度封装了与工业相机（特别是华睿相机）和基于串口通信的分拣设备的交互逻辑。通过本 SDK，开发者可快速集成包裹数据采集（条码、重量、尺寸、图像）、图像处理（保存、水印）和分拣控制功能。**分拣机指令由调用方自定义，SDK 只负责底层串口发送。**

#### **核心功能亮点：**

*   **多功能数据采集**：从相机实时捕获包裹的条码、重量和体积信息。
*   **图像智能处理**：支持相机捕获图像的自动保存，并可在图像上添加包含包裹详细信息的可定制水印。
*   **灵活的包裹过滤**：提供重量阈值过滤功能。
*   **事件驱动架构**：通过丰富的事件通知机制，使应用程序能够实时响应包裹状态、连接变化和操作结果。
*   **高内聚低耦合**：SDK 内部服务完全自包含，保证模块独立性。
*   **简单易用**：API 接口直观，分拣指令由调用方自定义，SDK 只负责发送。

### **2. 快速入门**

#### **2.1 项目引用**

在您的 .NET 项目中，通过以下方式添加对 `CangFenBao.SDK.dll` 的引用：

**方式一：通过 Visual Studio / Rider 添加引用**

1.  右键点击您的项目下的 `依赖项` (Dependencies) 或 `引用` (References)。
2.  选择 `添加项目引用` (Add Project Reference) 或 `添加引用` (Add Reference)。
3.  在弹出的窗口中，选择 `浏览` (Browse) 选项卡。
4.  导航到 `CangFenBao.SDK.dll` 所在的路径（通常在 `CangFenBao.SDK` 项目的 `bin/Debug/net8.0-windows` 或 `bin/Release/net8.0-windows` 目录下）。
5.  选中 `CangFenBao.SDK.dll` 并点击 `确定`。

**方式二：手动编辑 .csproj 文件**

在您的 `.csproj` 文件中，添加如下 `<Reference>` 节点：

```xml
<ItemGroup>
  <Reference Include="CangFenBao.SDK">
    <HintPath>Path\To\Your\CangFenBao.SDK.dll</HintPath>
  </Reference>
</ItemGroup>
```

**重要提示：** 请将 `Path\To\Your\CangFenBao.SDK.dll` 替换为 `CangFenBao.SDK.dll` 文件在您系统中的实际绝对或相对路径。确保您的应用程序能够找到此 DLL 文件，例如将其放置在应用程序的输出目录中。

#### **2.2 准备配置文件**

SDK 运行仅依赖两个必需的配置文件：

1.  **华睿相机配置文件 (`LogisticsBase.cfg`)**
    *   由相机 SDK 提供。
2.  **串口设置 (`SerialPortSettings.json`)**
    *   定义与分拣机通信的串口参数。
    *   示例：
        ```json
        {
          "PortName": "COM3",
          "BaudRate": 115200,
          "DataBits": 8,
          "StopBits": 1,
          "Parity": 0,
          "RtsEnable": false,
          "DtrEnable": false,
          "ReadTimeout": 500,
          "WriteTimeout": 500,
          "CommandDelayMs": 50
        }
        ```

> **分拣机正转/反转等所有控制指令均由调用方在业务层自行决定和传递，SDK 不再负责任何指令配置文件的加载。**

#### **2.3 代码示例**

```csharp
using CangFenBao.SDK;
using Common.Models.Package;
using System;
using System.IO;
using System.Threading.Tasks;

public class Program
{
    public static async Task Main(string[] args)
    {
        // 1. 定义 SDK 配置
        var config = new SdkConfig
        {
            HuaRayConfigPath = "D:\\LogisticsPlatform\\V1.0\\LogisticsBase.cfg",
            SerialPortSettingsPath = "D:\\Configs\\SerialPortSettings.json",
            MinimumWeightGrams = 20,
            SaveImages = true,
            ImageSavePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SavedImages"),
            AddWatermark = true,
            WatermarkFormat = "SN: {barcode}\nWeight: {weight}\nSize: {size}\nTime: {dateTime}",
            EnableUpload = true,
            UploadUrl = "http://yourserver.com/api/package",
            UploadImage = false
        };

        // 2. 创建并初始化 SDK 实例（串口参数由 SerialPortSettings.json 决定）
        await using var sdk = new SortingSystemSdk(
            config,
            "COM3", 115200, 8, 1, 0, 500, 500 // 串口参数示例
        );

        // 3. 订阅事件
        sdk.PackageReady += async (sender, package) =>
        {
            Console.WriteLine($"[有效包裹] 条码: {package.Barcode}, 重量: {package.Weight:F2}g, 图像路径: {package.ImagePath ?? "无"}.");
            // 业务层根据实际需求决定何时、如何发送分拣指令：
            // 例如：发送正转指令
            byte[] forwardCommand = new byte[] { 0xFE, 0x01, 0x01, 0xFF };
            await sdk.SendSorterCommandAsync(forwardCommand);
        };

        // 其他事件订阅（略）

        // 4. 初始化 SDK
        Console.WriteLine("正在初始化SDK，请确保所有配置文件路径正确...");
        bool initialized = await sdk.InitializeAsync();
        if (!initialized)
        {
            Console.Error.WriteLine("SDK 初始化失败，请检查配置文件路径、文件内容和设备连接。");
            return;
        }
        Console.WriteLine("SDK 初始化成功！");

        // 5. 启动服务
        Console.WriteLine("正在启动SDK所有服务（相机、分拣机），等待数据流...");
        await sdk.StartAsync();
        Console.WriteLine("SDK 已启动，正在等待包裹数据...");

        // 保持程序运行，直到用户按下Enter键
        Console.WriteLine("按 Enter 键停止并退出...");
        Console.ReadLine();

        // 6. SDK 资源自动释放（由于使用了 await using）
        Console.WriteLine("正在停止并释放SDK资源...");
    }
}
```

### **3. API 参考**

#### **3.1 `SDKConfig` 类**

| 属性名称             | 类型          | 说明                                                         | 默认值/示例                                                  |
| :------------------- | :------------ | :----------------------------------------------------------- | :----------------------------------------------------------- |
| `HuaRayConfigPath`   | `required string` | 华睿相机 `LogisticsBase.cfg` 文件的绝对路径。 | `"C:\\path\\to\\your\\LogisticsBase.cfg"` |
| `SerialPortSettingsPath` | `required string` | 串口设置 JSON 文件的绝对路径。 | `"C:\\path\\to\\your\\SerialPortSettings.json"` |
| `MinimumWeightGrams` | `double` | 最小重量阈值（单位：克）。**用于业务判断，融合后的包裹重量低于此值将被丢弃。** | `0` |
| `SaveImages`         | `bool`        | 是否保存相机捕获的图像到本地磁盘。 | `false` |
| `ImageSavePath`      | `string?`     | 图像保存的根目录路径。 | `null` |
| `AddWatermark`       | `bool`        | 是否在保存的图像上添加水印。 | `false` |
| `WatermarkFormat`    | `string`      | 定义水印内容的格式字符串。 | `"SN: {barcode} {dateTime}"` |
| `EnableUpload`       | `bool`        | 是否启用上传包裹数据到服务器的功能。 | `false` |
| `UploadUrl`          | `string?`     | 包裹数据上传的目标服务器URL。 | `null` |
| `UploadImage`        | `bool`        | 在上传包裹数据时，是否同时包含图像的 Base64 编码字符串。 | `false` |

#### **3.2 `SortingSystemSDK` 类**

##### **构造函数**

*   `public SortingSystemSDK(SDKConfig config, string portName, int baudRate, int dataBits, int stopBits, int parity, int readTimeout, int writeTimeout)`
    *   **参数**: `config` - SDK 配置；后续参数为串口参数。

##### **方法**

*   `public async Task<bool> SendSorterCommandAsync(byte[] command)`
    *   **说明**: 发送一条自定义分拣机指令。调用方需自行构造命令内容，SDK 只负责底层串口发送。
    *   **参数**: `command` - 要发送的指令字节数组。
    *   **返回**: `true` 表示指令发送成功，`false` 表示串口未连接或发送失败。

##### **事件**

*   `public event EventHandler<PackageInfo>? PackageReady;`
    *   **何时触发**: 当相机成功识别包裹，并且SDK完成重量数据融合后触发。
    *   **重要提示**: 此事件中 `PackageInfo` 对象的 `Weight` 属性是SDK内部通过稳定算法和时间窗口融合后的高精度重量，而非相机直接提供的原始值。
*   `public event EventHandler<(PackageInfo package, UploadResponse? response)>? UploadCompleted;`
*   `public event EventHandler<PackageInfo>? PackageDiscarded;`
*   `public event EventHandler<(BitmapSource Image, string CameraId)>? ImageReceived;`
*   `public event Action<string, bool>? CameraConnectionChanged;`
*   `public event Action<bool>? SorterConnectionChanged;`

---

> **分拣机所有控制指令均由调用方自定义，SDK 只负责发送。**

### **4. 日志配置**

`CangFenBao.SDK` 内置了基于 Serilog 的详细日志记录功能，用于监控和调试SDK的运行状态。

#### **4.1 日志特性**

*   **自动配置**: SDK 在初始化时自动配置 Serilog，无需额外配置。
*   **文件滚动**: 日志文件按日期自动滚动（每天创建新的日志文件）。
*   **大小限制**: 单个日志文件最大10MB，超过后自动创建新文件。
*   **保留策略**: 最多保留30天的历史日志文件，自动清理过期文件。
*   **多级日志**: 支持 Debug、Information、Warning、Error 等多个级别。

#### **4.2 日志文件位置**

日志文件保存在应用程序根目录下的 `logs` 文件夹中：

```
[应用程序目录]/
└── logs/
    ├── log-20241225.txt      // 当天日志
    ├── log-20241224.txt      // 昨天日志
    └── ...                   // 其他历史日志
```

#### **4.3 日志内容示例**

```
2024-12-25 10:30:15.123 +08:00 [INF] SDK 初始化开始...
2024-12-25 10:30:15.456 +08:00 [INF] SDK 初始化成功！
2024-12-25 10:30:15.789 +08:00 [INF] SDK 服务启动中...
2024-12-25 10:30:16.012 +08:00 [INF] 相机 'Camera_01' 连接状态: 在线
2024-12-25 10:30:16.234 +08:00 [INF] 分拣机串口连接状态: 已连接
2024-12-25 10:30:16.456 +08:00 [INF] SDK 服务已启动，正在等待包裹数据...
2024-12-25 10:30:20.789 +08:00 [INF] 收到原始包裹数据: 条码 ABC123456789, 重量 1250g
2024-12-25 10:30:20.890 +08:00 [INF] 包裹 ABC123456789 通过重量检查。
2024-12-25 10:30:20.901 +08:00 [INF] 启用上传功能，开始异步上传包裹 ABC123456789...
2024-12-25 10:30:21.123 +08:00 [INF] 开始上传包裹 ABC123456789 到 http://api.example.com/package
2024-12-25 10:30:21.345 +08:00 [INF] 包裹 ABC123456789 上传成功，服务器响应码: 0, 消息: 成功, 分配格口: 5
2024-12-25 10:30:21.456 +08:00 [INF] 收到服务器分拣指令: 包裹 ABC123456789 -> 格口 5
2024-12-25 10:30:21.567 +08:00 [INF] 开始处理包裹 ABC123456789 的分拣指令，目标格口: 5
2024-12-25 10:30:21.678 +08:00 [INF] 包裹 ABC123456789 的分拣指令已成功发送到队列。
```

#### **4.4 日志级别说明**

*   **Debug**: 详细的调试信息，如图像尺寸、数据大小等
*   **Information**: 重要的业务流程信息，如包裹处理、分拣指令等
*   **Warning**: 警告信息，如配置缺失、重量过低等
*   **Error**: 错误信息，如网络异常、文件读写失败等

#### **4.5 关键日志场景**

1.  **SDK 生命周期**: 初始化、启动、停止过程
2.  **设备连接**: 相机和串口连接状态变化
3.  **包裹处理**: 从接收到分拣的完整流程
4.  **图像处理**: 图像保存、水印添加过程
5.  **数据上传**: HTTP 请求和响应详情
6.  **配置管理**: 配置文件加载和保存
7.  **异常处理**: 各类异常的详细信息和上下文

通过查看日志文件，您可以：
*   监控SDK的运行状态
*   诊断硬件连接问题
*   追踪包裹处理过程
*   调试配置错误
*   分析性能问题

### **5. 内部服务（概览）**

为确保 SDK 的高内聚和独立性，以下服务被封装在 SDK 内部：

*   **`JsonSettingsService`**: 一个轻量级的 `Common.Services.Settings.ISettingsService` 实现，专门用于从 `SDKConfig` 指定的 JSON 文件加载设置。它确保 SDK 不会直接依赖于主应用程序的配置服务。
*   **`SdkImageService`**: 负责处理图像的保存和水印功能。它直接从 `SDKConfig` 读取图像相关的配置，并在后台线程执行图像处理任务，避免阻塞主数据流。
*   **`HttpUploadService`**: 处理所有与远程服务器的 HTTP 通信。它负责将 `PackageInfo` 对象转换为所需的 JSON 格式，处理图片 Base64 编码，并解析服务器响应。

这些内部服务的实现细节对 SDK 用户是透明的，您只需通过 `SDKConfig` 配置其行为。

### **6. 实时重量服务（WeightService）**

#### **6.1 WeightServiceSettings 配置项**

| 属性名称                 | 类型                | 说明                                   | 默认值/示例 |
|--------------------------|---------------------|----------------------------------------|-------------|
| `PortName`               | `string`            | 串口端口名                             | "COM3"     |
| `BaudRate`               | `int`               | 波特率                                 | 115200      |
| `DataBits`               | `int`               | 数据位                                 | 8           |
| `StopBits`               | `int`               | 停止位                                 | 1           |
| `Parity`                 | `int`               | 校验位（0=无，1=奇，2=偶）             | 0           |
| `ReadTimeout`            | `int`               | 读取超时时间（毫秒）                   | 500         |
| `WriteTimeout`           | `int`               | 写入超时时间（毫秒）                   | 500         |
| `CommandDelayMs`         | `int`               | 指令发送间隔（毫秒）                   | 50          |
| `MinimumValidWeight`     | `double`            | 最小有效重量阈值（克）。**用于原始秤数据过滤和稳定性判断，低于此值的原始重量将不参与融合。** | `20` |
| `EventIntervalMs`        | `int`               | 最小事件推送间隔（毫秒）               | 200         |
| `ProtocolType`           | `string`            | 协议类型（如 "Antto", "Zemic" 等）    | "Antto"    |
| `StableSampleCount` | `int` | 用于判断重量稳定性的滑动窗口样本数 | `5` |
| `StableThresholdGrams` | `double` | 判断重量稳定的阈值（克），窗口内所有值与最后一个值的差都小于此阈值视为稳定 | `10.0` |
| `FusionTimeRangeLowerMs` | `int` | 融合重量时，相对于相机数据时间戳的查找范围下限（毫秒，负值为向前查找） | `-500` |
| `FusionTimeRangeUpperMs` | `int` | 融合重量时，相对于相机数据时间戳的查找范围上限（毫秒，正值为向后查找） | `500` |

> **说明：**
> - 所有串口参数与主流工业秤一致，支持灵活配置。
> - `MinimumValidWeight` 用于过滤抖动和无效数据。
> - `EventIntervalMs` 控制事件推送频率，防止高频刷屏。

### 6.3 频率控制与阈值逻辑
- SDK 内部自动根据 `MinimumValidWeight` 过滤抖动和无效数据。
- 事件推送频率受 `EventIntervalMs` 控制，典型值 200ms。
- 支持多协议解析，自动适配主流工业秤。

### 6.5 生命周期管理说明
- 重量服务随 SortingSystemSDK 初始化、启动、停止、释放自动管理。
- 无需手动创建或销毁重量服务实例。
- 所有配置变更需重启 SDK 后生效。

### 6.6 常见问题与异常处理
- 串口占用、参数错误、协议不兼容等异常会自动记录日志并通过事件/日志反馈。
- 建议关注日志文件，及时发现硬件异常。

---
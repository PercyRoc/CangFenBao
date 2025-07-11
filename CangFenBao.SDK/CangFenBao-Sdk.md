## **CangFenBao.SDK 详细文档**

### **1. 概述**

`CangFenBao.SDK` 是一个专为物流分拣和包裹处理系统设计的 .NET 库。它高度封装了与工业相机（特别是华睿相机）和基于串口通信的小车分拣设备的复杂交互逻辑。通过使用本 SDK，开发者可以快速、高效地将包裹数据采集（条码、重量、尺寸、图像）、图像处理（保存、水印）和物理分拣控制功能集成到自己的应用程序中，而无需深入了解底层硬件协议和数据流细节。

#### **核心功能亮点：**

*   **多功能数据采集**：从相机实时捕获包裹的条码、重量和体积信息。
*   **图像智能处理**：支持相机捕获图像的自动保存，并可在图像上添加包含包裹详细信息的可定制水印，方便追溯和管理。
*   **灵活的包裹过滤**：提供重量阈值过滤功能，可自动识别并丢弃低于设定重量的无效包裹数据，提升数据质量。
*   **自动化数据上传与分拣**：能够将处理后的包裹数据（可选包含图像）自动上传至指定服务器，并根据服务器返回的分拣指令智能控制小车分拣机完成分拣任务。
*   **事件驱动架构**：通过丰富的事件通知机制，使应用程序能够实时响应包裹状态、连接变化和操作结果。
*   **高内聚低耦合**：SDK 内部服务（如配置加载、图像处理、HTTP通信）完全自包含，不侵入原有项目结构，保证模块独立性。
*   **简单易用**：提供简洁的配置类和直观的 API 接口，大幅降低集成难度。

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

SDK 的正常运行依赖于四个必需的 JSON 格式配置文件（华睿相机配置文件除外）。请确保这些文件存在于您指定的绝对路径，并根据您的实际硬件和业务需求进行配置。

1.  **华睿相机配置文件 (`LogisticsBase.cfg`)**
    *   **说明**: 这是华睿相机 SDK 所需的原生配置文件，通常由相机供应商提供或通过其专用配置工具生成。SDK 将加载此文件来初始化相机。
    *   **示例**: 无特定格式，由相机 SDK 决定。

2.  **串口设置 (`SerialPortSettings.json`)**
    *   **说明**: 定义了与小车分拣控制器通信所需的串口参数，如端口号、波特率等。
    *   **示例 `SerialPortSettings.json`**: 
        ```json
        {
          "PortName": "COM3",       // 串口名称，例如 COM1, COM3
          "BaudRate": 115200,       // 波特率
          "DataBits": 8,            // 数据位
          "StopBits": 1,            // 停止位: 0=None, 1=One, 2=Two, 3=OnePointFive
          "Parity": 0,              // 校验方式: 0=None, 1=Odd, 2=Event, 3=Mark, 4=Space
          "RtsEnable": false,       // 是否启用RTS流控制
          "DtrEnable": false,       // 是否启用DTR流控制
          "ReadTimeout": 500,       // 读取超时时间(ms)
          "WriteTimeout": 500,      // 写入超时时间(ms)
          "CommandDelayMs": 50      // 命令延迟发送时间(ms)，命令将在触发后等待此时间后再发送
        }
        ```

3.  **小车硬件参数 (`CarSettings.json`)**
    *   **说明**: 包含了系统中每个物理小车的独特硬件参数，例如运行速度、加速度、延迟等。
    *   **示例 `CarSettings.json`**: 
        ```json
        {
          "CarConfigs": [
            {
              "Name": "1号小车",      // 小车名称，用于识别
              "Address": 1,           // 小车地址（唯一标识）
              "Speed": 500,           // 运行速度
              "Time": 500,            // 运行时间(ms)
              "Acceleration": 6,      // 加速度
              "Delay": 350            // 延迟运行时间(ms)
            },
            {
              "Name": "2号小车",
              "Address": 2,
              "Speed": 500,
              "Time": 500,
              "Acceleration": 6,
              "Delay": 350
            }
            // ... 更多小车配置
          ]
        }
        ```

4.  **小车分拣序列 (`CarSequenceSettings.json`)**
    *   **说明**: 定义了包裹被投递到特定格口（Chute）时，需要联动哪些小车以及它们各自的动作顺序和延时。这允许实现复杂的联动分拣逻辑。
    *   **示例 `CarSequenceSettings.json`**: 
        ```json
        {
          "ChuteSequences": [
            {
              "ChuteNumber": 1,       // 目标格口号
              "CarSequence": [        // 触发此格口所需的小车动作序列
                {
                  "CarAddress": 1,      // 小车地址
                  "IsReverse": false,   // 是否反向运行
                  "DelayMs": 0,         // 发送此小车命令前的延迟时间(ms)
                  "CarName": "1号小车"  // 对应小车名称（可选，仅用于显示/日志）
                },
                {
                  "CarAddress": 2,
                  "IsReverse": true,    // 反向
                  "DelayMs": 50,        // 延迟50ms后发送
                  "CarName": "2号小车"
                }
              ]
            },
            {
              "ChuteNumber": 2,
              "CarSequence": [
                {
                  "CarAddress": 3,
                  "IsReverse": false,
                  "DelayMs": 0,
                  "CarName": "3号小车"
                }
              ]
            }
            // ... 更多格口与小车序列配置
          ]
        }
        ```

#### **2.3 代码示例**

以下是一个完整的控制台应用程序示例，演示了如何实例化、配置（包括新添加的图像处理和数据上传设置）和使用 `CangFenBao.SDK`。

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
            // --- 必需的配置文件路径 ---
            HuaRayConfigPath = "D:\\LogisticsPlatform\\V1.0\\LogisticsBase.cfg", // 替换为您的实际路径
            SerialPortSettingsPath = "D:\\Configs\\SerialPortSettings.json",     // 替换为您的实际路径
            CarSettingsPath = "D:\\Configs\\CarSettings.json",                   // 替换为您的实际路径
            CarSequenceSettingsPath = "D:\\Configs\\CarSequenceSettings.json",   // 替换为您的实际路径

            // --- 包裹过滤配置 (可选) ---
            // 如果包裹重量小于20克，将被SDK自动丢弃，不触发 PackageReady 事件。
            // 设置为 0 或负数禁用此功能。
            MinimumWeightGrams = 20, 

            // --- 图像处理与保存配置 (可选) ---
            SaveImages = true,    // 是否保存相机捕获的图像
            ImageSavePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SavedImages"), // 图像保存目录
            AddWatermark = true,  // 是否在保存的图像上添加水印
            // 水印格式：支持 {barcode}, {weight}, {size}, {dateTime} 占位符
            WatermarkFormat = "SN: {barcode}\nWeight: {weight}\nSize: {size}\nTime: {dateTime}",

            // --- 数据上传配置 (可选) ---
            EnableUpload = true,    // 是否启用上传包裹数据功能
            UploadUrl = "http://yourserver.com/api/package", // 您的服务器上传API地址
            UploadImage = false     // 上传时是否包含图像Base64数据 (会显著增加请求体大小)
        };

        // 2. 创建并初始化 SDK 实例
        // 使用 await using 确保 SDK 资源在离开作用域时被正确释放
        await using var sdk = new SortingSystemSdk(config);

        // 3. 订阅 SDK 事件
        // 当相机识别到有效包裹信息后触发，此时包裹重量已通过过滤，并已进行图像处理（保存/水印）
        sdk.PackageReady += async (sender, package) =>
        {
            Console.WriteLine($"[有效包裹] 条码: {package.Barcode}, 重量: {package.Weight:F2}g, 图像路径: {package.ImagePath ?? "无"}.");
            // 在启用 EnableUpload 时，此事件后会立即尝试上传和自动分拣，
            // 此时不应再手动调用 sdk.SortPackageAsync(package)，否则可能重复分拣。
            // 如果 EnableUpload=false，您可以在此事件中根据业务逻辑手动设置 package.ChuteNumber 并调用 SortPackageAsync。
        };

        // (新) 当包裹因重量过低而被丢弃时触发
        sdk.PackageDiscarded += (sender, package) =>
        {
            Console.WriteLine($"[丢弃包裹] 包裹因重量 ({package.Weight}g) 低于阈值 ({config.MinimumWeightGrams}g) 被丢弃。条码: {package.Barcode}");
        };

        // 当相机捕获到图像时触发（无论是有效包裹还是被过滤的包裹，只要有图像都会触发）
        sdk.ImageReceived += (sender, data) =>
        {
            Console.WriteLine($"[图像事件] 收到来自相机 {data.CameraId} 的图像，尺寸: {data.Image.PixelWidth}x{data.Image.PixelHeight}.");
            // data.Image 是一个 BitmapSource 对象，可用于实时显示等。
        };

        // (新) 包裹数据上传完成时触发，无论上传成功或失败
        sdk.UploadCompleted += (sender, result) =>
        {
            var (package, response) = result;
            if (response is { Code: 0, Chute: > 0 })
            {
                Console.WriteLine($"[上传成功] 包裹 {package.Barcode} 已成功上传。服务器指令格口: {response.Chute}。");
                // SDK 将根据此指令自动执行分拣。
            }
            else
            {
                Console.Error.WriteLine($"[上传失败] 包裹 {package.Barcode} 上传失败或服务器未返回有效格口。错误码: {response?.Code}, 消息: {response?.Message ?? "无响应或网络错误"}");
            }
        };

        // 相机连接状态变化时触发
        sdk.CameraConnectionChanged += (id, status) => Console.WriteLine($"[状态] 相机 '{id}' 连接状态: {(status ? "在线" : "离线")}");
        
        // 分拣机（小车串口）连接状态变化时触发
        sdk.SorterConnectionChanged += (status) => Console.WriteLine($"[状态] 分拣机串口连接状态: {(status ? "已连接" : "已断开")}");

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

`SDKConfig` 是初始化 `SortingSystemSDK` 时必须提供的配置类，它包含了 SDK 行为所需的所有参数。

| 属性名称             | 类型          | 说明                                                         | 默认值/示例                                                  |
| :------------------- | :------------ | :----------------------------------------------------------- | :----------------------------------------------------------- |
| `HuaRayConfigPath`   | `required string` | 华睿相机 `LogisticsBase.cfg` 文件的绝对路径。这是华睿相机 SDK 必需的原生配置文件。 | `"C:\\path\\to\\your\\LogisticsBase.cfg"`                      |
| `SerialPortSettingsPath` | `required string` | 串口设置 JSON 文件的绝对路径。详见 `2.2 准备配置文件 - 串口设置`。 | `"C:\\path\\to\\your\\SerialPortSettings.json"`              |
| `CarSettingsPath`    | `required string` | 小车硬件参数 JSON 文件的绝对路径。详见 `2.2 准备配置文件 - 小车硬件参数`。 | `"C:\\path\\to\\your\\CarSettings.json"`                    |
| `CarSequenceSettingsPath` | `required string` | 小车分拣序列 JSON 文件的绝对路径。详见 `2.2 准备配置文件 - 小车分拣序列`。 | `"C:\\path\\to\\your\\CarSequenceSettings.json"`              |
| `MinimumWeightGrams` | `double`      | 最小重量阈值（单位：克）。如果包裹重量（由相机测得）小于此值，将被视为无效包裹并丢弃。<br/>设置为 `0` 或负数可禁用此功能。 | `0`                                                          |
| `SaveImages`         | `bool`        | 是否保存相机捕获的图像到本地磁盘。如果为 `true`，`ImageSavePath` 必须提供。 | `false`                                                      |
| `ImageSavePath`      | `string?`     | 图像保存的根目录路径。当 `SaveImages` 为 `true` 时，SDK 将在此路径下按 `年/月/日` 的结构创建子目录来组织图片。 | `null` (但如果 `SaveImages` 为 `true` 则为必需)                |
| `AddWatermark`       | `bool`        | 是否在保存的图像上添加水印。此设置仅在 `SaveImages` 为 `true` 时生效。 | `false`                                                      |
| `WatermarkFormat`    | `string`      | 定义水印内容的格式字符串。支持以下占位符：<br/>- `{barcode}`: 包裹主条码<br/>- `{weight}`: 包裹重量 (例如 "1.23kg")<br/>- `{size}`: 包裹尺寸 (例如 "10.0*20.5*5.1cm")<br/>- `{dateTime}`: 处理时间 (例如 "2023-10-27 10:30:00") | `"SN: {barcode} {dateTime}"`                                 |
| `EnableUpload`       | `bool`        | 是否启用上传包裹数据到服务器的功能。如果为 `true`，SDK 将在包裹有效后自动执行上传。 | `false`                                                      |
| `UploadUrl`          | `string?`     | 包裹数据上传的目标服务器URL。当 `EnableUpload` 为 `true` 时，此项必须提供。 | `null` (但如果 `EnableUpload` 为 `true` 则为必需)              |
| `UploadImage`        | `bool`        | 在上传包裹数据时，是否同时包含图像的 Base64 编码字符串。请注意，这会显著增加 HTTP 请求体的大小和网络负担。 | `false`                                                      |

#### **3.2 `SortingSystemSDK` 类**

这是与 `CangFenBao.SDK` 交互的核心入口点。

##### **构造函数**

*   `public SortingSystemSDK(SDKConfig config)`
    *   **参数**: `config` - 用于初始化 SDK 的 `SDKConfig` 实例。

##### **事件**

*   `public event EventHandler<PackageInfo>? PackageReady;`
    *   **何时触发**: 当相机成功识别并解析出有效包裹（通过重量过滤）时触发。此时，包裹的条码、重量、尺寸等基础信息已填充，并且图像处理（保存、水印）已在后台启动。
    *   **用途**: 您可以在此事件中进行业务逻辑处理，例如显示包裹信息、记录日志。
    *   **重要提示**: 如果 `SDKConfig.EnableUpload` 为 `true`，SDK 会在此事件后自动触发数据上传和分拣，您通常无需在此事件中手动调用 `SortPackageAsync`。

*   `public event EventHandler<PackageInfo>? PackageDiscarded;`
    *   **何时触发**: 当从相机收到的包裹重量低于 `SDKConfig.MinimumWeightGrams` 设定的阈值时触发。此包裹将被 SDK 自动丢弃，不会进入后续处理流程，也不会触发 `PackageReady` 事件。
    *   **用途**: 用于监控和记录无效的包裹数据。

*   `public event EventHandler<(BitmapSource Image, string CameraId)>? ImageReceived;`
    *   **何时触发**: 每当相机捕获到新的图像时触发，无论该图像是否与有效包裹关联（即即使包裹因重量被过滤，只要有图像，此事件仍可能触发）。
    *   **用途**: 允许您获取并显示实时相机图像流，进行监控或调试。

*   `public event EventHandler<(PackageInfo package, UploadResponse? response)>? UploadCompleted;`
    *   **何时触发**: 当 `SDKConfig.EnableUpload` 为 `true` 时，每次包裹数据上传尝试完成后触发，无论上传成功或失败。
    *   **参数**: 
        *   `package`: 尝试上传的 `PackageInfo` 对象。
        *   `response`: 服务器返回的 `UploadResponse` 对象。如果上传过程中发生网络错误、请求失败（非2xx状态码）或 JSON 解析失败，`response` 将为 `null`。
    *   **用途**: 用于监控数据上传状态，处理上传结果，或在上传失败时触发重试/报警逻辑。

*   `public event Action<string, bool>? CameraConnectionChanged;`
    *   **何时触发**: 相机设备的连接状态发生变化时触发（例如，相机上线或离线）。
    *   **参数**: `string` - 相机 ID；`bool` - 连接状态（`true` 为在线，`false` 为离线）。

*   `public event Action<bool>? SorterConnectionChanged;`
    *   **何时触发**: 分拣机（小车串口）的连接状态发生变化时触发。
    *   **参数**: `bool` - 连接状态（`true` 为已连接，`false` 为已断开）。

##### **属性**

*   `public bool IsRunning { get; }`
    *   指示 SDK 当前是否处于运行状态（即相机和分拣服务是否已启动）。

##### **方法**

*   `public async Task<bool> InitializeAsync()`
    *   **说明**: 异步初始化 SDK 内部的所有服务和依赖项（包括相机、小车分拣服务和内部数据处理服务）。
    *   **重要性**: 在调用 `StartAsync` 方法之前，此方法必须成功完成。
    *   **返回**: `true` 表示初始化成功；`false` 表示初始化失败（通常因配置文件缺失、无效或设备连接问题）。

*   `public async Task StartAsync()`
    *   **说明**: 启动所有已初始化的服务，开始接收相机数据并根据配置处理分拣逻辑。
    *   **前置条件**: 必须在 `InitializeAsync()` 成功完成后调用。

*   `public async Task StopAsync()`
    *   **说明**: 停止所有正在运行的 SDK 服务，停止数据采集和分拣处理。

*   `public async Task<bool> SortPackageAsync(PackageInfo package)`
    *   **说明**: 将一个包含目标格口号的包裹信息加入分拣队列，SDK 将尝试向小车分拣机发送相应的分拣指令。
    *   **参数**: `package` - 必须是一个已设置 `ChuteNumber` 属性的 `PackageInfo` 对象。
    *   **返回**: `true` 表示分拣指令已成功加入队列并发送（不代表硬件执行成功）；`false` 表示服务未运行或参数无效。
    *   **使用场景**: 主要用于 `SDKConfig.EnableUpload` 为 `false` 的手动分拣模式，或在服务器未返回格口指令时，应用程序需进行备用分拣。

*   `public async Task<bool> SortToChuteAsync(int chuteNumber)`
    *   **说明**: 绕过正常的包裹处理流程，直接向指定的格口发送一次小车分拣指令。主要用于测试或调试特定格口功能。
    *   **参数**: `chuteNumber` - 目标格口号。
    *   **返回**: `true` 表示命令发送成功；`false` 表示服务未运行或命令发送失败。

*   `public IEnumerable<CameraBasicInfo> GetAvailableCameras()`
    *   **说明**: 获取当前系统（由华睿相机 SDK 发现）中可用的相机列表及其基本信息。
    *   **返回**: `CameraBasicInfo` 对象的集合。

*   `public async ValueTask DisposeAsync()`
    *   **说明**: 异步释放 SDK 使用的所有内部资源，包括停止所有服务、取消订阅和清理对象。
    *   **重要性**: 当您使用 `await using var sdk = new SortingSystemSDK(config);` 语句创建 SDK 实例时，此方法会在 `using` 代码块结束时自动调用，无需手动管理。

---

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
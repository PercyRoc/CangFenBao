## **CangFenBao.SDK Detailed Documentation**

**Table of Contents**

*   [Overview](#1-overview)
    *   [Core Feature Highlights](#core-feature-highlights)
*   [Quick Start](#2-quick-start)
    *   [Project Reference](#21-project-reference)
    *   [Preparing Configuration Files](#22-preparing-configuration-files)
    *   [Code Example](#23-code-example)
*   [API Reference](#3-api-reference)
    *   [`SDKConfig` Class](#31-sdkconfig-class)
    *   [`SortingSystemSDK` Class](#32-sortingsystemsdk-class)
        *   [Constructor](#constructor)
        *   [Methods](#methods)
        *   [Events](#events)
*   [Logging Configuration](#4-logging-configuration)
    *   [Logging Features](#41-logging-features)
    *   [Log File Location](#42-log-file-location)
    *   [Log Content Example](#43-log-content-example)
    *   [Log Level Descriptions](#44-log-level-descriptions)
    *   [Key Logging Scenarios](#45-key-logging-scenarios)
*   [Internal Services (Overview)](#5-internal-services-overview)
*   [Real-time Weight Service (WeightService)](#6-real-time-weight-service-weightservice)
    *   [WeightServiceSettings Configuration Items](#61-weightservicesettings-configuration-items)
    *   [Frequency Control and Threshold Logic](#63-frequency-control-and-threshold-logic)
    *   [Lifecycle Management](#65-lifecycle-management)
    *   [Common Issues and Exception Handling](#66-common-issues-and-exception-handling)

### **1. Overview**

`CangFenBao.SDK` is a .NET library specifically designed for logistics sorting and package processing systems. It provides a high-level encapsulation of the interaction logic with industrial cameras (especially HuaRay cameras) and sorting devices based on serial communication. With this SDK, developers can quickly integrate functionalities for package data acquisition (barcode, weight, dimensions, image), image processing (saving, watermarking), and sorting control. **Sorting machine commands are custom-defined by the caller; the SDK is only responsible for the low-level serial port transmission.**

#### **Core Feature Highlights:**

*   **Multi-functional Data Acquisition**: Captures package barcode, weight, and volume information from the camera in real-time.
*   **Intelligent Image Processing**: Supports automatic saving of images captured by the camera and allows adding customizable watermarks with detailed package information.
*   **Flexible Package Filtering**: Provides weight threshold filtering functionality.
*   **Event-Driven Architecture**: Enables applications to respond in real-time to package status, connection changes, and operation results through a rich event notification mechanism.
*   **High Cohesion, Low Coupling**: Internal SDK services are fully self-contained, ensuring module independence.
*   **Simple and Easy to Use**: Features an intuitive API. Sorting commands are defined by the caller, and the SDK only handles the transmission.

### **2. Quick Start**

#### **2.1 Project Reference**

In your .NET project, add a reference to `CangFenBao.SDK.dll` in one of the following ways:

**Method 1: Add Reference via Visual Studio / Rider**

1.  Right-click on `Dependencies` or `References` in your project.
2.  Select `Add Project Reference` or `Add Reference`.
3.  In the pop-up window, select the `Browse` tab.
4.  Navigate to the path where `CangFenBao.SDK.dll` is located (usually in the `bin/Debug/net8.0-windows` or `bin/Release/net8.0-windows` directory of the `CangFenBao.SDK` project).
5.  Select `CangFenBao.SDK.dll` and click `OK`.

**Method 2: Manually Edit the .csproj File**

In your `.csproj` file, add the following `<Reference>` node:

```xml
<ItemGroup>
  <Reference Include="CangFenBao.SDK">
    <HintPath>Path\To\Your\CangFenBao.SDK.dll</HintPath>
  </Reference>
</ItemGroup>
```

**Important Note:** Please replace `Path\To\Your\CangFenBao.SDK.dll` with the actual absolute or relative path to the `CangFenBao.SDK.dll` file on your system. Ensure your application can find this DLL file, for example, by placing it in the application's output directory.

#### **2.2 Preparing Configuration Files**

The SDK requires only two configuration files to run:

1.  **HuaRay Camera Configuration File (`LogisticsBase.cfg`)**
    *   Provided by the camera SDK.
2.  **Serial Port Settings (`SerialPortSettings.json`)**
    *   Defines the serial port parameters for communication with the sorting machine.
    *   Example:
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

> **All control commands for the sorting machine, such as forward/reverse rotation, are determined and passed by the caller at the business layer. The SDK is no longer responsible for loading any command configuration files.**

#### **2.3 Code Example**

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
        // 1. Define SDK Configuration
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

        // 2. Create and initialize the SDK instance (serial port parameters are determined by SerialPortSettings.json)
        await using var sdk = new SortingSystemSdk(
            config,
            "COM3", 115200, 8, 1, 0, 500, 500 // Example serial port parameters
        );

        // 3. Subscribe to events
        sdk.PackageReady += async (sender, package) =>
        {
            Console.WriteLine($"[Valid Package] Barcode: {package.Barcode}, Weight: {package.Weight:F2}g, Image Path: {package.ImagePath ?? "None"}.");
            // The business layer decides when and how to send sorting commands based on actual needs:
            // For example: send a forward rotation command
            byte[] forwardCommand = new byte[] { 0xFE, 0x01, 0x01, 0xFF };
            await sdk.SendSorterCommandAsync(forwardCommand);
        };

        // Other event subscriptions (omitted)

        // 4. Initialize the SDK
        Console.WriteLine("Initializing SDK, please ensure all configuration file paths are correct...");
        bool initialized = await sdk.InitializeAsync();
        if (!initialized)
        {
            Console.Error.WriteLine("SDK initialization failed. Please check config file paths, content, and device connections.");
            return;
        }
        Console.WriteLine("SDK initialized successfully!");

        // 5. Start services
        Console.WriteLine("Starting all SDK services (camera, sorter), waiting for data stream...");
        await sdk.StartAsync();
        Console.WriteLine("SDK started, waiting for package data...");

        // Keep the program running until the user presses Enter
        Console.WriteLine("Press Enter to stop and exit...");
        Console.ReadLine();

        // 6. SDK resources are automatically released (due to 'await using')
        Console.WriteLine("Stopping and releasing SDK resources...");
    }
}
```

### **3. API Reference**

#### **3.1 `SDKConfig` Class**

| Property Name        | Type              | Description                                                                 | Default/Example                                            |
| :------------------- | :---------------- | :-------------------------------------------------------------------------- | :--------------------------------------------------------- |
| `HuaRayConfigPath`   | `required string` | The absolute path to the HuaRay camera `LogisticsBase.cfg` file.          | `"C:\\path\\to\\your\\LogisticsBase.cfg"`                  |
| `SerialPortSettingsPath` | `required string` | The absolute path to the serial port settings JSON file.                  | `"C:\\path\\to\\your\\SerialPortSettings.json"`            |
| `MinimumWeightGrams` | `double`          | The minimum weight threshold (in grams). **Used for business logic; fused packages below this weight will be discarded.** | `0`                                                        |
| `SaveImages`         | `bool`            | Whether to save images captured by the camera to the local disk.            | `false`                                                    |
| `ImageSavePath`      | `string?`         | The root directory path for saving images.                                  | `null`                                                     |
| `AddWatermark`       | `bool`            | Whether to add a watermark to the saved images.                             | `false`                                                    |
| `WatermarkFormat`    | `string`          | A format string that defines the content of the watermark.                  | `"SN: {barcode} {dateTime}"`                               |
| `EnableUpload`       | `bool`            | Whether to enable the feature to upload package data to a server.           | `false`                                                    |
| `UploadUrl`          | `string?`         | The target server URL for uploading package data.                           | `null`                                                     |
| `UploadImage`        | `bool`            | Whether to include a Base64 encoded string of the image when uploading package data. | `false`                                                    |

#### **3.2 `SortingSystemSDK` Class**

##### **Constructor**

*   `public SortingSystemSDK(SDKConfig config, string portName, int baudRate, int dataBits, int stopBits, int parity, int readTimeout, int writeTimeout)`
    *   **Parameters**: `config` - SDK configuration; subsequent parameters are for the serial port.

##### **Methods**

*   `public async Task<bool> SendSorterCommandAsync(byte[] command)`
    *   **Description**: Sends a custom command to the sorting machine. The caller must construct the command content; the SDK is only responsible for the low-level serial transmission.
    *   **Parameters**: `command` - The byte array of the command to be sent.
    *   **Returns**: `true` if the command was sent successfully, `false` if the serial port is not connected or the send failed.

##### **Events**

*   `public event EventHandler<PackageInfo>? PackageReady;`
    *   **When Triggered**: Triggered when the camera successfully identifies a package and the SDK completes the weight data fusion.
    *   **Important Note**: The `Weight` property of the `PackageInfo` object in this event is a high-precision weight obtained through the SDK's internal stabilization algorithm and time window fusion, not the raw value directly provided by the camera.
*   `public event EventHandler<(PackageInfo package, UploadResponse? response)>? UploadCompleted;`
*   `public event EventHandler<PackageInfo>? PackageDiscarded;`
*   `public event EventHandler<(BitmapSource Image, string CameraId)>? ImageReceived;`
*   `public event Action<string, bool>? CameraConnectionChanged;`
*   `public event Action<bool>? SorterConnectionChanged;`

---

> **All control commands for the sorting machine are custom-defined by the caller; the SDK is only responsible for sending them.**

### **4. Logging Configuration**

`CangFenBao.SDK` has built-in detailed logging based on Serilog to monitor and debug the SDK's operational status.

#### **4.1 Logging Features**

*   **Automatic Configuration**: The SDK automatically configures Serilog during initialization, requiring no extra setup.
*   **File Rolling**: Log files are automatically rolled over by date (a new log file is created daily).
*   **Size Limit**: A single log file has a maximum size of 10MB; a new file is created when this limit is exceeded.
*   **Retention Policy**: Historical log files are retained for a maximum of 30 days, and expired files are automatically cleaned up.
*   **Multi-level Logging**: Supports multiple levels, including Debug, Information, Warning, and Error.

#### **4.2 Log File Location**

Log files are saved in the `logs` folder in the application's root directory:

```
[Application Directory]/
└── logs/
    ├── log-20241225.txt      // Today's log
    ├── log-20241224.txt      // Yesterday's log
    └── ...                   // Other historical logs
```

#### **4.3 Log Content Example**

```
2024-12-25 10:30:15.123 +08:00 [INF] SDK initialization started...
2024-12-25 10:30:15.456 +08:00 [INF] SDK initialized successfully!
2024-12-25 10:30:15.789 +08:00 [INF] Starting SDK services...
2024-12-25 10:30:16.012 +08:00 [INF] Camera 'Camera_01' connection status: Online
2024-12-25 10:30:16.234 +08:00 [INF] Sorter serial port connection status: Connected
2024-12-25 10:30:16.456 +08:00 [INF] SDK services started, waiting for package data...
2024-12-25 10:30:20.789 +08:00 [INF] Received raw package data: Barcode ABC123456789, Weight 1250g
2024-12-25 10:30:20.890 +08:00 [INF] Package ABC123456789 passed weight check.
2024-12-25 10:30:20.901 +08:00 [INF] Upload enabled, starting asynchronous upload for package ABC123456789...
2024-12-25 10:30:21.123 +08:00 [INF] Starting upload for package ABC123456789 to http://api.example.com/package
2024-12-25 10:30:21.345 +08:00 [INF] Package ABC123456789 uploaded successfully, server response code: 0, message: Success, assigned chute: 5
2024-12-25 10:30:21.456 +08:00 [INF] Received sorting command from server: Package ABC123456789 -> Chute 5
2024-12-25 10:30:21.567 +08:00 [INF] Starting to process sorting command for package ABC123456789, target chute: 5
2024-12-25 10:30:21.678 +08:00 [INF] Sorting command for package ABC123456789 has been successfully sent to the queue.
```

#### **4.4 Log Level Descriptions**

*   **Debug**: Detailed debugging information, such as image dimensions, data size, etc.
*   **Information**: Important business process information, such as package processing, sorting commands, etc.
*   **Warning**: Warning messages, such as missing configurations, low weight, etc.
*   **Error**: Error messages, such as network exceptions, file I/O failures, etc.

#### **4.5 Key Logging Scenarios**

1.  **SDK Lifecycle**: Initialization, start, and stop processes.
2.  **Device Connection**: Status changes for camera and serial port connections.
3.  **Package Processing**: The complete flow from reception to sorting.
4.  **Image Processing**: Image saving and watermarking processes.
5.  **Data Upload**: Details of HTTP requests and responses.
6.  **Configuration Management**: Loading and saving of configuration files.
7.  **Exception Handling**: Detailed information and context for various exceptions.

By reviewing the log files, you can:
*   Monitor the SDK's operational status.
*   Diagnose hardware connection issues.
*   Track the package processing flow.
*   Debug configuration errors.
*   Analyze performance problems.

### **5. Internal Services (Overview)**

To ensure the high cohesion and independence of the SDK, the following services are encapsulated within it:

*   **`JsonSettingsService`**: A lightweight implementation of `Common.Services.Settings.ISettingsService`, specifically for loading settings from the JSON file specified in `SDKConfig`. It ensures that the SDK does not directly depend on the main application's configuration service.
*   **`SdkImageService`**: Responsible for handling image saving and watermarking. It reads image-related configurations directly from `SDKConfig` and performs image processing tasks in a background thread to avoid blocking the main data flow.
*   **`HttpUploadService`**: Handles all HTTP communication with the remote server. It is responsible for converting `PackageInfo` objects into the required JSON format, handling Base64 encoding of images, and parsing server responses.

The implementation details of these internal services are transparent to the SDK user; you only need to configure their behavior through `SDKConfig`.

### **6. Real-time Weight Service (WeightService)**

#### **6.1 WeightServiceSettings Configuration Items**

| Property Name            | Type     | Description                                                                 | Default/Example |
|--------------------------|----------|-----------------------------------------------------------------------------|-----------------|
| `PortName`               | `string` | Serial port name                                                            | "COM3"          |
| `BaudRate`               | `int`    | Baud rate                                                                   | 115200          |
| `DataBits`               | `int`    | Data bits                                                                   | 8               |
| `StopBits`               | `int`    | Stop bits                                                                   | 1               |
| `Parity`                 | `int`    | Parity (0=None, 1=Odd, 2=Even)                                              | 0               |
| `ReadTimeout`            | `int`    | Read timeout (milliseconds)                                                 | 500             |
| `WriteTimeout`           | `int`    | Write timeout (milliseconds)                                                | 500             |
| `CommandDelayMs`         | `int`    | Command sending interval (milliseconds)                                     | 50              |
| `MinimumValidWeight`     | `double` | Minimum valid weight threshold (grams). **Used for raw scale data filtering and stability checks; raw weights below this value will not be used in fusion.** | `20`              |
| `EventIntervalMs`        | `int`    | Minimum event push interval (milliseconds)                                  | 200             |
| `ProtocolType`           | `string` | Protocol type (e.g., "Antto", "Zemic")                                      | "Antto"         |
| `StableSampleCount`      | `int`    | The number of samples in the sliding window used to determine weight stability. | `5`               |
| `StableThresholdGrams`   | `double` | The threshold (in grams) for determining weight stability. The weight is considered stable if the difference between all values in the window and the last value is less than this threshold. | `10.0`            |
| `FusionTimeRangeLowerMs` | `int`    | The lower bound of the search range (in milliseconds) relative to the camera data timestamp for weight fusion (a negative value means searching backward). | `-500`            |
| `FusionTimeRangeUpperMs` | `int`    | The upper bound of the search range (in milliseconds) relative to the camera data timestamp for weight fusion (a positive value means searching forward). | `500`             |

> **Notes:**
> - All serial port parameters are compatible with mainstream industrial scales and are flexibly configurable.
> - `MinimumValidWeight` is used to filter out jitter and invalid data.
> - `EventIntervalMs` controls the event push frequency to prevent high-frequency updates.

### 6.3 Frequency Control and Threshold Logic
- The SDK internally filters out jitter and invalid data based on `MinimumValidWeight`.
- The event push frequency is controlled by `EventIntervalMs`, typically 200ms.
- Supports multi-protocol parsing, automatically adapting to mainstream industrial scales.

### 6.5 Lifecycle Management
- The weight service is automatically managed along with the initialization, start, stop, and disposal of the `SortingSystemSDK`.
- There is no need to manually create or destroy instances of the weight service.
- All configuration changes require restarting the SDK to take effect.

### 6.6 Common Issues and Exception Handling
- Exceptions such as serial port occupation, parameter errors, and protocol incompatibility are automatically logged and reported through events/logs.
- It is recommended to monitor the log files to promptly detect hardware anomalies.

--- 
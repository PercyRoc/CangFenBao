# WeiCiModule - 卫慈模组带分拣系统

## 概述
WeiCiModule 是一个基于 WPF 和 Prism 框架开发的模组带分拣系统，专门为卫慈项目设计。系统集成了相机识别、TCP通信、条码映射和自动分拣功能。

## 主要功能

### 1. 模组带分拣服务 (ModuleConnectionService)
- **TCP服务器**: 监听来自PLC控制器的连接和数据包
- **包裹序号处理**: 接收PLC发送的包裹触发信号
- **分拣指令发送**: 根据包裹信息向PLC发送分拣指令
- **时间管理**: 使用Stopwatch避免时区问题，精确计算包裹等待时间
- **异常处理**: 支持超时包裹的异常格口分拣

### 2. 相机集成
- 集成TCP相机服务，实时接收包裹图像数据
- 支持条码识别和"NOREAD"无码处理
- 图像数据与分拣逻辑联动

### 3. 条码映射系统
- 支持从TXT文件导入条码到图书馆分馆的映射关系
- 灵活的格口配置，支持图书馆分馆代码到物理格口的映射
- 异常包裹自动分配到异常格口

### 4. 配置管理
- **TCP设置**: IP地址、端口、等待时间范围配置
- **格口设置**: 图书馆分馆与格口编号的映射配置
- **条码映射**: 支持导入和搜索条码映射关系

## 核心特性

### 时区问题解决方案
系统采用 `Stopwatch` 来测量时间间隔，避免了不同地区服务器时区配置差异导致的时间计算问题：

```csharp
// 使用Stopwatch替代DateTime计算，避免时区问题
var timeDiff = waitInfo.ElapsedStopwatch.ElapsedMilliseconds;
```

### TCP通信协议
- **起始码**: 0xF9
- **功能码**: 
  - 0x10: 接收包裹序号
  - 0x11: 发送分拣指令  
  - 0x12: 反馈指令
- **数据包长度**: 8字节
- **校验码**: 0xFF

### 包裹处理流程
1. 相机识别包裹条码
2. 根据条码查找图书馆分馆映射
3. 根据分馆配置确定目标格口
4. 等待PLC触发信号
5. 在有效时间窗口内匹配包裹
6. 发送分拣指令到PLC
7. 处理PLC反馈确认

## 项目结构
```
WeiCiModule/
├── Models/
│   ├── Settings/
│   │   ├── ModelsTcpSettings.cs          # TCP连接设置
│   │   └── ChuteSettings.cs              # 格口配置
│   ├── BarcodeChuteMapping.cs            # 条码格口映射
│   └── ChuteSettingData.cs               # 格口设置数据
├── Services/
│   ├── IModuleConnectionService.cs       # 模组带服务接口
│   ├── ModuleConnectionService.cs        # 模组带服务实现
│   └── ExcelService.cs                   # Excel导入导出
├── ViewModels/
│   ├── MainViewModel.cs                  # 主界面ViewModel
│   ├── SettingsDialogViewModel.cs        # 设置对话框ViewModel
│   ├── ChuteSettingsViewModel.cs         # 格口设置ViewModel
│   └── Settings/
│       └── ModulesTcpSettingsViewModel.cs # TCP设置ViewModel
└── Views/
    ├── MainWindow.xaml                   # 主界面
    ├── SettingsDialog.xaml               # 设置对话框
    ├── ChuteSettingsView.xaml            # 格口设置界面
    └── Settings/
        └── ModulesTcpSettingsView.xaml   # TCP设置界面
```

## 配置说明

### TCP设置 (ModelsTcpSettings.json)
```json
{
  "Address": "192.168.1.100",     // TCP服务器监听地址
  "Port": 8080,                   // 监听端口
  "MinWaitTime": 100,             // 最小等待时间(ms)
  "MaxWaitTime": 2000,            // 最大等待时间(ms)
  "ExceptionChute": 32            // 异常格口号
}
```

### 格口设置 (ChuteSettings.json)
```json
{
  "Items": [
    {
      "SN": 1,
      "BranchCode": "AMKPL", 
      "Branch": "Ang Mo Kio Public Library"
    }
  ]
}
```

## 部署要求
- .NET 8.0 Runtime
- Windows操作系统
- 相机设备支持TCP通信
- PLC控制器支持自定义TCP协议

## 故障排除

### 常见问题
1. **时间匹配失败**: 检查系统时区设置，系统已使用Stopwatch避免时区问题
2. **TCP连接失败**: 确认IP地址和端口配置正确，防火墙允许通信
3. **包裹匹配超时**: 调整MinWaitTime和MaxWaitTime参数
4. **条码映射失败**: 检查导入的TXT文件格式是否为 `^条码^,^分馆名称^`

### 日志查看
系统使用Serilog记录详细日志，日志文件位于 `logs/` 目录下。

## 维护说明
- 定期备份配置文件
- 监控日志文件大小和异常记录
- 更新条码映射数据时建议先备份现有配置 
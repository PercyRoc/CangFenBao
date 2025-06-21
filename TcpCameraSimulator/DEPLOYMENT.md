# TCP相机模拟器 - 部署说明 📦

本文档指导您如何在**没有开发环境**的电脑上部署和使用TCP相机模拟器。

## 🎯 部署目标

将测试工具打包成**自包含可执行文件**，可以直接复制到任何Windows电脑上运行，无需安装.NET运行时。

## 📋 系统要求

### 目标电脑要求（运行测试的电脑）
- **操作系统**: Windows 10/11 (x64)
- **架构**: 64位系统
- **内存**: 建议 4GB 以上
- **磁盘空间**: 至少 200MB 可用空间
- **网络**: 能访问被测试的服务器

### 开发电脑要求（打包的电脑）
- **操作系统**: Windows 10/11
- **.NET SDK**: .NET 8.0 或更高版本
- **磁盘空间**: 至少 500MB 可用空间

## 🚀 打包步骤

### 1. 在开发电脑上打包

```bash
# 进入项目目录
cd TcpCameraSimulator

# 运行发布脚本（推荐）
publish.bat

# 或手动执行发布命令
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

### 2. 打包结果

发布完成后，会在 `publish` 文件夹中生成以下文件：

```
publish/
├── TcpCameraSimulator.exe    # 主程序 (60-80MB)
├── test-scripts.bat          # 快速测试脚本
├── README.md                 # 使用说明
└── (其他必要的依赖文件)
```

## 📦 部署到目标电脑

### 方法一：完整复制（推荐）
```bash
# 将整个 publish 文件夹复制到目标电脑
# 例如复制到: D:\Tools\TcpCameraSimulator\
```

### 方法二：压缩包分发
```bash
# 将 publish 文件夹打包为 ZIP
# 在目标电脑解压到任意位置
```

## 🔧 在目标电脑上使用

### 1. 快速测试（推荐）
```bash
# 双击运行批处理脚本
test-scripts.bat

# 选择测试场景：
# 4. PLC+相机协调测试 (推荐用于背压验证)
```

### 2. 命令行使用
```bash
# 打开 PowerShell 或 CMD，进入程序目录
cd D:\Tools\TcpCameraSimulator

# 查看帮助
TcpCameraSimulator.exe --help

# 基础协调测试
TcpCameraSimulator.exe --coordinated --rate 10 --duration 180

# 高并发测试
TcpCameraSimulator.exe --clients 10 --rate 50 --duration 120

# 自定义服务器地址
TcpCameraSimulator.exe --coordinated --plc-host 192.168.1.100 --host 192.168.1.101
```

## 📊 使用场景示例

### 场景1：验证背压修复（生产环境）
```bash
# 在生产服务器所在网络的电脑上运行
TcpCameraSimulator.exe --coordinated --rate 15 --plc-host 192.168.1.100 --plc-port 20010 --host 192.168.1.100 --port 20011 --duration 300

# 关注指标：
# - 信号匹配率 > 99%
# - 无136秒延迟
# - P99延迟 < 200ms
```

### 场景2：现场压力测试
```bash
# 高强度压测模式
TcpCameraSimulator.exe --stress --clients 5 --burst 100 --duration 180

# 监控服务器性能和响应时间
```

### 场景3：长期稳定性验证
```bash
# 低频长期测试（建议在非工作时间进行）
TcpCameraSimulator.exe --coordinated --rate 5 --duration 3600

# 运行1小时，验证无内存泄漏和性能衰减
```

## 🔍 故障排除

### 常见问题

#### 1. "应用程序无法启动"
**原因**: 目标电脑不是64位系统或缺少运行库
**解决**: 
- 确认目标电脑是Windows x64系统
- 安装 Microsoft Visual C++ Redistributable

#### 2. "找不到网络路径"
**原因**: 网络配置或防火墙问题
**解决**: 
- 检查网络连通性：`ping 目标服务器IP`
- 检查端口开放：`telnet 目标服务器IP 端口`
- 临时关闭防火墙测试

#### 3. "连接被拒绝"
**原因**: 目标服务未启动或端口错误
**解决**: 
- 确认服务器上的相机服务和PLC服务正在运行
- 验证端口配置（默认：PLC 20010，相机 20011）

#### 4. 程序运行但无输出
**原因**: 控制台缓冲或权限问题
**解决**: 
- 以管理员身份运行
- 使用 PowerShell 而不是 CMD
- 重定向输出：`TcpCameraSimulator.exe --help > output.txt`

### 调试技巧

#### 1. 启用详细日志
```bash
# 程序已内置详细的控制台日志
# 运行时按 's' 键查看详细统计
# 按 'l' 键查看延迟分析
```

#### 2. 网络连通性测试
```bash
# 测试PLC服务器连通性
telnet PLC服务器IP 20010

# 测试相机服务器连通性  
telnet 相机服务器IP 20011
```

#### 3. 分步测试
```bash
# 1. 先测试标准模式
TcpCameraSimulator.exe --clients 1 --rate 1 --duration 30

# 2. 再测试协调模式
TcpCameraSimulator.exe --coordinated --rate 1 --duration 30

# 3. 逐步增加负载
```

## 📝 部署清单

### 发布前检查
- [ ] 项目编译无错误
- [ ] 发布脚本执行成功
- [ ] 生成的exe文件可以正常运行
- [ ] test-scripts.bat 功能正常
- [ ] README.md 内容完整

### 部署前检查
- [ ] 目标电脑系统兼容（Windows x64）
- [ ] 网络环境配置正确
- [ ] 目标服务器可访问
- [ ] 防火墙规则配置
- [ ] 用户权限充足

### 测试验证
- [ ] 程序可以正常启动
- [ ] 命令行参数解析正确
- [ ] 网络连接功能正常
- [ ] 统计报告输出正确
- [ ] 协调模式工作正常

## 🎯 性能优化建议

### 对于高频测试
- 建议在服务器同网段的电脑上运行
- 使用有线网络连接，避免WiFi
- 关闭不必要的防病毒软件实时扫描

### 对于长期测试
- 确保电脑不会自动休眠或关机
- 监控电脑的CPU和内存使用率
- 定期查看测试输出和统计报告

## 📞 技术支持

如果在部署过程中遇到问题：

1. **查看程序帮助**: `TcpCameraSimulator.exe --help`
2. **查看使用说明**: 打开 `README.md` 文件
3. **查看实时统计**: 程序运行时按 `s` 键
4. **查看延迟分析**: 程序运行时按 `l` 键

---

**重要提醒**: 本工具专为TCP相机服务的背压问题验证设计，协调模式能最真实地复现生产环境，建议优先使用协调模式进行测试。 
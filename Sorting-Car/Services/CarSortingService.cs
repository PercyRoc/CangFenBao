using Common.Models.Package;
using Common.Services.SerialPort;
using Common.Services.Settings;
using Serilog;
using Sorting_Car.Models;

namespace Sorting_Car.Services
{
    /// <summary>
    /// 小车分拣服务实现
    /// </summary>
    public class CarSortingService : ICarSortingDevice
    {
        private const byte DualFrameParamFrame = 0x95; // 双帧控制 - 参数设定帧 (无回码)
        private const byte DualFrameRunFrame = 0x8A; // 双帧控制 - 运行命令帧 (广播，无回码)
        private const int CommandSendDelayMs = 50; // 命令之间增加少量延迟

        private readonly ISettingsService _settingsService;
        private readonly object _sendLock = new(); // 保证命令按顺序发送
        private byte _sequenceCounter; // 用于 0x8A 运行命令帧的序列号

        private SerialPortService? _serialPortService;
        private CarConfigModel? _carConfigModel;
        private CarSequenceSettings? _carSequenceSettings;
        private CarSerialPortSettings? _serialPortSettings;
        private bool _isInitialized;
        private CancellationTokenSource? _serviceShutdownCts;

        public event EventHandler<(string DeviceName, bool IsConnected)>? ConnectionChanged;

        public CarSortingService(ISettingsService settingsService)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            Log.Information("CarSortingService 已创建");
        }

        public bool IsConnected => _serialPortService?.IsConnected ?? false;

        public Task<bool> StartAsync()
        {
            if (_isInitialized)
            {
                Log.Information("CarSortingService 已初始化");
                return Task.FromResult(true);
            }

            if (_serviceShutdownCts == null || _serviceShutdownCts.IsCancellationRequested)
            {
                 _serviceShutdownCts = new CancellationTokenSource();
            }

            Log.Information("CarSortingService 初始化开始...");
            try
            {
                // 1. 加载配置
                Log.Debug("正在加载 CarSerialPortSettings...");
                _serialPortSettings = _settingsService.LoadSettings<CarSerialPortSettings>();
                Log.Debug("正在加载 CarConfigModel...");
                _carConfigModel = _settingsService.LoadSettings<CarConfigModel>();
                Log.Debug("正在加载 CarSequenceSettings...");
                _carSequenceSettings = _settingsService.LoadSettings<CarSequenceSettings>();

                if (_serialPortSettings == null || _carConfigModel == null || _carSequenceSettings == null)
                {
                    Log.Error("初始化失败：必要的配置加载不完整");
                    return Task.FromResult(false);
                }

                Log.Information("配置加载完成。串口端口: {PortName}", _serialPortSettings.PortName);
                Log.Information("已加载 {Count} 个小车配置", _carConfigModel.CarConfigs?.Count ?? 0);
                Log.Information("已加载 {Count} 个格口序列配置", _carSequenceSettings.ChuteSequences.Count);
                if (_serialPortService != null)
                {
                    _serialPortService.Dispose();
                    _serialPortService = null;
                }

                var serialParams = ConvertToSerialPortParams(_serialPortSettings);
                _serialPortService = new SerialPortService("CarSorting", serialParams);
                _serialPortService.ConnectionChanged += OnConnectionChanged;
                _serialPortService.DataReceived += OnDataReceived;

                Log.Information("尝试连接串口 {PortName}...", _serialPortSettings.PortName);
                var connected = _serialPortService.Connect();
                if (!connected)
                {
                    Log.Error("串口 {PortName} 连接失败", _serialPortSettings.PortName);
                    if (_serialPortService != null)
                    {
                        _serialPortService.ConnectionChanged -= OnConnectionChanged;
                        _serialPortService.DataReceived -= OnDataReceived;
                        _serialPortService.Dispose();
                        _serialPortService = null;
                    }
                    return Task.FromResult(false);
                }

                _isInitialized = true;
                Log.Information("CarSortingService 初始化成功，串口已连接");
                return Task.FromResult(true);
            }
            catch (OperationCanceledException)
            {
                Log.Information("CarSortingService 初始化操作被取消。");
                _isInitialized = false;
                return Task.FromResult(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "CarSortingService 初始化失败");
                if (_serialPortService != null)
                {
                    _serialPortService.ConnectionChanged -= OnConnectionChanged;
                    _serialPortService.DataReceived -= OnDataReceived;
                    _serialPortService.Dispose();
                    _serialPortService = null;
                }
                _isInitialized = false;
                return Task.FromResult(false);
            }
        }

        private static SerialPortParams ConvertToSerialPortParams(CarSerialPortSettings settings)
        {
            Log.Debug("将 CarSerialPortSettings 转换为 SerialPortParams");
            return new SerialPortParams
            {
                PortName = settings.PortName,
                BaudRate = settings.BaudRate,
                DataBits = settings.DataBits,
                StopBits = settings.StopBits switch {
                    SerialStopBits.One => System.IO.Ports.StopBits.One,
                    SerialStopBits.Two => System.IO.Ports.StopBits.Two,
                    SerialStopBits.OnePointFive => System.IO.Ports.StopBits.OnePointFive,
                    _ => System.IO.Ports.StopBits.None,
                },
                Parity = settings.Parity switch {
                    SerialParity.None => System.IO.Ports.Parity.None,
                    SerialParity.Odd => System.IO.Ports.Parity.Odd,
                    SerialParity.Even => System.IO.Ports.Parity.Even,
                    SerialParity.Mark => System.IO.Ports.Parity.Mark,
                    SerialParity.Space => System.IO.Ports.Parity.Space,
                    _ => System.IO.Ports.Parity.None,
                },
            };
        }

        private void OnConnectionChanged(bool isConnected)
        {
            Log.Information("串口连接状态变更: {Status}", isConnected ? "已连接" : "已断开");
            ConnectionChanged?.Invoke(this, ("CarSorting", isConnected));
        }

        private void OnDataReceived(byte[] data)
        {
            Log.Debug("收到串口数据: {DataHex}", BitConverter.ToString(data));
        }

        public bool SendCommandForPackage(PackageInfo package, CancellationToken cancellationToken = default)
        {
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _serviceShutdownCts?.Token ?? CancellationToken.None);
            var linkedToken = linkedCts.Token;

            if (package.ChuteNumber > 0)
                return SendCommandForChute(package.ChuteNumber, linkedToken);

            Log.Warning("包裹 {Barcode} 的格口号无效 ({ChuteNumber})，无法发送命令", package.Barcode, package.ChuteNumber);
            return false;
        }

        private bool SendCommandForChute(int chuteNumber, CancellationToken cancellationToken)
        {
            if (!_isInitialized || _serialPortService == null)
            {
                Log.Error("服务未初始化或串口服务不可用，无法为格口 {ChuteNumber} 发送命令", chuteNumber);
                return false;
            }

            if (!IsConnected)
            {
                Log.Error("串口未连接，无法为格口 {ChuteNumber} 发送命令", chuteNumber);
                return false;
            }

            if (_carSequenceSettings?.ChuteSequences == null)
            {
                Log.Error("格口序列配置未加载，无法为格口 {ChuteNumber} 发送命令", chuteNumber);
                return false;
            }

            var chuteSequence = _carSequenceSettings.ChuteSequences.FirstOrDefault(cs => cs.ChuteNumber == chuteNumber);
            if (chuteSequence == null || chuteSequence.CarSequence.Count == 0)
            {
                Log.Warning("未找到格口 {ChuteNumber} 的小车序列配置，或序列为空", chuteNumber);
                return false;
            }

            Log.Information("开始为格口 {ChuteNumber} 发送 {Count} 条双帧小车命令(16字节)...", chuteNumber,
                chuteSequence.CarSequence.Count);
            var overallSuccess = true;

            lock (_sendLock)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Log.Information("格口 {ChuteNumber} 命令发送在进入锁后被取消。", chuteNumber);
                    return false;
                }
                if (!IsConnected)
                {
                    Log.Error("在发送锁内检测到串口断开，格口 {ChuteNumber} 命令发送中止", chuteNumber);
                    return false;
                }

                foreach (var carItem in chuteSequence.CarSequence)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Log.Information("格口 {ChuteNumber} 的小车序列发送在处理小车 {Address} 前被取消。", chuteNumber, carItem.CarAddress);
                        overallSuccess = false;
                        break;
                    }
                    try
                    {
                        if (carItem.DelayMs > 0)
                        {
                            Log.Debug("等待小车 {Address} 的自定义延迟: {DelayMs}ms", carItem.CarAddress, carItem.DelayMs);
                            if (Task.Delay(carItem.DelayMs, cancellationToken).Wait(TimeSpan.FromMilliseconds(carItem.DelayMs + 100), cancellationToken))
                            {
                                // 延迟完成
                            }
                            else if (cancellationToken.IsCancellationRequested)
                            {
                                Log.Debug("小车 {Address} 延迟期间被取消");
                                overallSuccess = false; break;
                            }
                        }

                        var car = _carConfigModel?.CarConfigs?.FirstOrDefault(c => c.Address == carItem.CarAddress);
                        var runTime = car?.Time ?? 500;

                        var command = GenerateCombinedDualFrameCommand(carItem.CarAddress, carItem.IsReverse, runTime);
                        if (command.Length == 16)
                        {
                            Log.Debug("发送组合双帧命令(16字节 0x95+0x8A)给小车地址 {Address} (反转={IsReverse}): {CommandHex}",
                                carItem.CarAddress, carItem.IsReverse, BitConverter.ToString(command));

                            if (cancellationToken.IsCancellationRequested) { overallSuccess = false; break; }
                            if (!IsConnected)
                            {
                                Log.Error("在发送命令给小车 {Address} 之前检测到串口断开，格口 {ChuteNumber} 发送中止", carItem.CarAddress, chuteNumber);
                                overallSuccess = false;
                                break;
                            }

                            var sent = _serialPortService.Send(command);
                            if (!sent)
                            {
                                Log.Error("发送组合命令到小车 {Address} 失败", carItem.CarAddress);
                                overallSuccess = false;
                            }
                            else
                            {
                                if (Task.Delay(CommandSendDelayMs, cancellationToken).Wait(TimeSpan.FromMilliseconds(CommandSendDelayMs + 100), cancellationToken))
                                {
                                    // 延迟完成
                                }
                                else if (cancellationToken.IsCancellationRequested)
                                {
                                    Log.Debug("命令间延迟期间被取消");
                                    overallSuccess = false; break;
                                }
                            }
                        }
                        else
                        {
                            Log.Warning("为小车地址 {Address} 生成的组合命令无效，跳过发送", carItem.CarAddress);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Information("处理或发送小车 {Address} 命令时操作被取消。", carItem.CarAddress);
                        overallSuccess = false;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "处理或发送小车 {Address} 组合命令时发生异常", carItem.CarAddress);
                        overallSuccess = false;
                    }
                }
            }

            Log.Information("格口 {ChuteNumber} 的小车组合命令序列发送完成，总体结果: {Result}", chuteNumber,
                overallSuccess ? "成功" : "部分/全部失败");
            return overallSuccess;
        }

        /// <summary>
        /// 生成组合的双帧控制命令 (16字节: 0x95参数帧 + 0x8A运行帧)
        /// </summary>
        /// <param name="addr">目标小车地址 (1-32)</param>
        /// <param name="isReverse">是否反向运行 (true=反向, false=正向) - 用于0x95帧</param>
        /// <param name="timeMs">运行时间 (ms) - 用于0x95帧</param>
        /// <returns>16字节的命令数组，如果参数无效则为空数组</returns>
        private byte[] GenerateCombinedDualFrameCommand(byte addr, bool isReverse, int timeMs)
        {
            // 1. 参数校验 (地址、速度、延迟、时间等)
            if (_carConfigModel?.CarConfigs == null)
            {
                Log.Error("小车配置未加载，无法为地址 {Address} 生成命令", addr);
                return [];
            }

            var car = _carConfigModel.CarConfigs.FirstOrDefault(c => c.Address == addr);
            if (car == null)
            {
                Log.Warning("未找到小车地址 {Address} 的配置，无法生成命令", addr);
                return [];
            }

            if (addr < 1 || addr > 32) // 地址范围扩大到1-32以包含0x8A帧的寻址
            {
                Log.Warning("无效的小车地址 {Address} (必须在 1-32 之间)", addr);
                return [];
            }

            if (car.Speed < 30 || car.Speed > 1530)
            {
                Log.Warning("无效的速度 {Speed} RPM (必须在 30-1530 之间)", car.Speed);
                return [];
            }

            if (car.Delay < 0 || car.Delay > 2550)
            {
                Log.Warning("无效的延迟时间 {DelayMs} ms (必须在 0-2550 ms 之间)", car.Delay);
                return [];
            }

            if (timeMs < 0 || timeMs > 2550)
            {
                Log.Warning("无效的运行时间 {TimeMs} ms (必须在 0-2550 ms 之间)", timeMs);
                return [];
            }

            try
            {
                var cmd = new byte[16]; // 创建16字节数组

                // --- 第1部分: 0x95 参数设定帧 (cmd[0] - cmd[7]) ---
                const int piValue = 1; // 固定PI值为1

                cmd[0] = DualFrameParamFrame; // 帧头 0x95

                // Byte 1: 地址 + 方向 + S7
                byte speedVal = (byte)Math.Clamp(car.Speed / 6, 5, 255); // 速度计算
                byte s7 = (byte)(speedVal >> 7 & 0x01); // 获取速度的最高位
                byte directionBit = (byte)(isReverse ? 0x40 : 0x00); // 方向位 (0x00=正向, 0x40=反向)
                cmd[1] = (byte)(directionBit | s7 << 5 | addr & 0x1F); // 注意: 0x95帧地址只用到低5位(1-31)

                // Byte 2: 速度 S6-S0
                cmd[2] = (byte)(speedVal & 0x7F); // 速度低7位

                // Byte 3: 延迟 Dt6-Dt0
                byte delayVal = (byte)Math.Clamp(car.Delay / 10, 0, 255);
                byte dt7 = (byte)(delayVal >> 7 & 0x01); // 获取延迟的最高位
                cmd[3] = (byte)(delayVal & 0x7F); // 获取延迟的低7位

                // Byte 4: 时间 T6-T0
                byte timeVal = (byte)Math.Clamp(timeMs / 10, 0, 255);
                byte t7 = (byte)(timeVal >> 7 & 0x01); // 获取时间的最高位
                cmd[4] = (byte)(timeVal & 0x7F); // 获取时间的低7位

                // Byte 5: 复合数据 1
                const byte piBits = piValue - 1 & 0x07; // PI值1对应0
                const byte controlModeBit = 0x00; // 时间模式 Bit2=0
                cmd[5] = (byte)(piBits << 3 | controlModeBit | t7 << 1 | dt7);

                // Byte 6: 复合数据 2 (暂不用)
                cmd[6] = 0x00;

                // Byte 7: CRC校验 (0x95 帧)
                cmd[7] = CalculateCrc(cmd, 1, 6); // 字节 1-6 的CRC


                // --- 第2部分: 0x8A 运行命令帧 (cmd[8] - cmd[15]) ---
                cmd[8] = DualFrameRunFrame; // 帧头 0x8A

                // Bytes 9-13: 根据地址 addr (1-32) 设置对应的位
                cmd[9] = 0; // Reg 1: 小车 7-1
                cmd[10] = 0; // Reg 2: 小车 15-9
                cmd[11] = 0; // Reg 3: 小车 23-17
                cmd[12] = 0; // Reg 4: 小车 31-25
                cmd[13] = 0; // Reg 5: 小车 32,24,16,8

                if (addr <= 7) // Reg 1
                {
                    cmd[9] = (byte)(1 << addr - 1);
                }
                else if (addr is >= 9 and <= 15) // Reg 2
                {
                    cmd[10] = (byte)(1 << addr - 9);
                }
                else if (addr is >= 17 and <= 23) // Reg 3
                {
                    cmd[11] = (byte)(1 << addr - 17);
                }
                else if (addr is >= 25 and <= 31) // Reg 4
                {
                    cmd[12] = (byte)(1 << addr - 25);
                }
                else // Reg 5
                {
                    if (addr == 8) cmd[13] = 1 << 0;
                    else if (addr == 16) cmd[13] = 1 << 1;
                    else if (addr == 24) cmd[13] = 1 << 2;
                    else cmd[13] = 1 << 3;
                }

                // Byte 14: 序列号 (递增, B7=0)
                cmd[14] = (byte)(_sequenceCounter & 0x7F); // 取低7位确保 B7=0
                _sequenceCounter++; // 递增计数器
                if (_sequenceCounter >= 128) // 循环使用 0-127
                {
                    _sequenceCounter = 0;
                }

                // Byte 15: CRC校验 (0x8A 帧)
                cmd[15] = CalculateCrc(cmd, 9, 14); // 字节 9-14 的CRC

                Log.Debug("生成的组合双帧命令(16字节): {CommandHex}", BitConverter.ToString(cmd));
                return cmd;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "为地址 {Address} 生成组合双帧命令时发生异常", addr);
                return [];
            }
        }

        /// <summary>
        /// 计算CRC校验码 (指定范围字节的异或和 & 0x7F)
        /// </summary>
        /// <param name="data">包含数据的字节数组</param>
        /// <param name="startIndex">计算起始索引 (包含)</param>
        /// <param name="endIndex">计算结束索引 (包含)</param>
        /// <returns>校验码</returns>
        private static byte CalculateCrc(byte[] data, int startIndex, int endIndex)
        {
            if (data == null || data.Length <= endIndex || startIndex < 0 || startIndex > endIndex)
            {
                throw new ArgumentException("CRC计算的输入参数无效");
            }

            byte crc = 0;
            for (int i = startIndex; i <= endIndex; i++)
            {
                crc ^= data[i];
            }

            return (byte)(crc & 0x7F); // 根据规范，最后与0x7F进行与操作
        }

        public Task<bool> StopAsync()
        {
            if (!_isInitialized)
            {
                Log.Information("CarSortingService 未运行或未初始化，无需停止。");
                return Task.FromResult(true);
            }

            Log.Information("CarSortingService 正在停止...");
            try
            {
                _serviceShutdownCts?.Cancel();

                _serialPortService?.Disconnect();

                if (_serialPortService != null)
                {
                    _serialPortService.ConnectionChanged -= OnConnectionChanged;
                    _serialPortService.DataReceived -= OnDataReceived;
                    _serialPortService.Dispose();
                    _serialPortService = null;
                    Log.Information("SerialPortService 已断开并释放。");
                }

                _isInitialized = false;
                Log.Information("CarSortingService 已停止。");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "停止 CarSortingService 时发生异常");
                _isInitialized = false;
                return Task.FromResult(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Log.Information("正在释放 CarSortingService...");
            
            await StopAsync();

            _serviceShutdownCts?.Dispose();
            _serviceShutdownCts = null;
            Log.Debug("CancellationTokenSource 已释放。");

            if (_serialPortService != null)
            {
                Log.Warning("在 DisposeAsync 期间 SerialPortService 不为 null，现在执行释放。");
                _serialPortService.ConnectionChanged -= OnConnectionChanged;
                _serialPortService.DataReceived -= OnDataReceived;
                _serialPortService.Dispose();
                _serialPortService = null;
            }

            _isInitialized = false;

            Log.Information("CarSortingService 已释放。");
            GC.SuppressFinalize(this);
        }
    }
}
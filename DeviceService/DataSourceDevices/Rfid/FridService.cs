using DeviceService.DataSourceDevices.Rfid.Sdk;
using Serilog;
using Common.Models.Settings;

namespace DeviceService.DataSourceDevices.Rfid;

/// <summary>
/// Frid设备服务实现
/// </summary>
public class FridService : IFridService
{
    private RfidReader? _reader;
    private bool _disposed;

    public bool IsConnected { get; private set; }

    public bool IsEnabled { get; private set; }

    public event Action<bool>? ConnectionChanged;
    public event Action<FridTagData>? TagDataReceived;

    public Task<bool> InitializeAsync(FridSettings settings)
    {
        try
        {
            IsEnabled = settings.IsEnabled;

            if (!IsEnabled)
            {
                Log.Information("Frid设备已禁用");
                return Task.FromResult(true);
            }

            Log.Information("初始化Frid设备，连接类型: {ConnectionType}", settings.ConnectionType);

            // 创建通知实现
            var notifyImpl = new FridReaderNotifyImpl(this);

            // 根据连接类型创建Reader
            _reader = settings.ConnectionType switch
            {
                FridConnectionType.Tcp => CreateTcpReader(settings, notifyImpl),
                FridConnectionType.SerialPort => CreateSerialReader(settings, notifyImpl),
                _ => throw new ArgumentException($"不支持的连接类型: {settings.ConnectionType}")
            };

            if (_reader == null)
            {
                Log.Error("创建Frid Reader失败");
                return Task.FromResult(false);
            }

            Log.Information("Frid设备初始化成功");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化Frid设备失败");
            return Task.FromResult(false);
        }
    }

    public Task<bool> ConnectAsync()
    {
        if (_reader == null || !IsEnabled)
        {
            Log.Warning("Frid设备未初始化或已禁用");
            return Task.FromResult(false);
        }

        try
        {
            Log.Information("连接Frid设备...");

            // 请求资源
            if (!_reader.RequestResource())
            {
                Log.Error("请求Frid设备资源失败");
                return Task.FromResult(false);
            }

            IsConnected = true;
            ConnectionChanged?.Invoke(true);
            Log.Information("Frid设备连接成功");

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "连接Frid设备失败");
            IsConnected = false;
            ConnectionChanged?.Invoke(false);
            return Task.FromResult(false);
        }
    }

    public Task DisconnectAsync()
    {
        if (_reader == null)
            return Task.CompletedTask;

        try
        {
            Log.Information("断开Frid设备连接...");
            _reader.ReleaseResource();
            IsConnected = false;
            ConnectionChanged?.Invoke(false);
            Log.Information("Frid设备已断开连接");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "断开Frid设备连接时发生错误");
        }

        return Task.CompletedTask;
    }

    public Task<bool> SetWorkingParamAsync(int power)
    {
        if (_reader == null || !IsConnected)
        {
            Log.Warning("Frid设备未连接，无法设置参数");
            return Task.FromResult(false);
        }

        try
        {
            Log.Information("设置Frid设备功率: {Power} dBm", power);

            // 创建工作参数
            var workParam = new RfidWorkParam
            {
                ucRFPower = (byte)power,
                ucParamVersion = 1,
                ucScanInterval = 100,
                ucAutoTrigoffTime = 0,
                ucWorkMode = 0, // 连续模式
                ucInventoryArea = 0,
                ucInventoryAddress = 0,
                ucInventoryLength = 0,
                ucFilterTime = 0,
                ucBeepOnFlag = 0,
                ucIsEnableRecord = 0,
                usAntennaFlag = 1,
                usDeviceAddr = 0
            };

            // 设置工作参数
            _reader.SetWorkingParam(workParam);

            Log.Information("Frid设备功率设置成功");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设置Frid设备功率失败");
            return Task.FromResult(false);
        }
    }

    public Task<bool> StartInventoryAsync()
    {
        if (_reader == null || !IsConnected)
        {
            Log.Warning("Frid设备未连接，无法开始盘点");
            return Task.FromResult(false);
        }

        try
        {
            Log.Information("开始Frid设备盘点...");
            _reader.StartInventory();
            Log.Information("Frid设备盘点已开始");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "开始Frid设备盘点失败");
            return Task.FromResult(false);
        }
    }

    public Task<bool> StopInventoryAsync()
    {
        if (_reader == null || !IsConnected)
        {
            Log.Warning("Frid设备未连接，无法停止盘点");
            return Task.FromResult(false);
        }

        try
        {
            Log.Information("停止Frid设备盘点...");
            _reader.StopInventory();
            Log.Information("Frid设备盘点已停止");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止Frid设备盘点失败");
            return Task.FromResult(false);
        }
    }

    public Task<FridDeviceInfo?> QueryDeviceInfoAsync()
    {
        if (_reader == null || !IsConnected)
        {
            Log.Warning("Frid设备未连接，无法查询设备信息");
            return Task.FromResult<FridDeviceInfo?>(null);
        }

        try
        {
            Log.Information("查询Frid设备信息...");
            _reader.QueryDeviceInfo();

            // 注意：这里需要等待回调返回设备信息
            // 在实际实现中，可能需要使用TaskCompletionSource来等待回调
            // 这里简化处理，直接返回null
            return Task.FromResult<FridDeviceInfo?>(null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "查询Frid设备信息失败");
            return Task.FromResult<FridDeviceInfo?>(null);
        }
    }

    private static RfidReader? CreateTcpReader(FridSettings settings, FridReaderNotifyImpl notifyImpl)
    {
        try
        {
            Log.Information("创建TCP连接Frid Reader: {IpAddress}:{Port}", 
                settings.TcpIpAddress, settings.TcpPort);

            return RfidReaderManager.Instance().CreateReaderInNet(
                settings.TcpIpAddress, // 本地IP
                (ushort)settings.TcpPort, // 本地端口
                settings.TcpIpAddress, // 远程IP
                (ushort)settings.TcpPort, // 远程端口
                TransportType.TcpClient,
                notifyImpl
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "创建TCP连接Frid Reader失败");
            return null;
        }
    }

    private RfidReader? CreateSerialReader(FridSettings settings, FridReaderNotifyImpl notifyImpl)
    {
        try
        {
            Log.Information("创建串口连接Frid Reader: {PortName}, {BaudRate}", 
                settings.SerialPortName, settings.BaudRate);

            return RfidReaderManager.Instance().CreateReaderInSerialPort(
                settings.SerialPortName,
                settings.BaudRate,
                notifyImpl
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "创建串口连接Frid Reader失败");
            return null;
        }
    }

    internal void OnTagDataReceived(TlvValueItem[] tlvItems, byte tlvCount)
    {
        try
        {
            var tagData = new FridTagData
            {
                ReadTime = DateTime.Now
            };

            // 解析TLV数据
            for (int i = 0; i < tlvCount; i++)
            {
                var item = tlvItems[i];
                switch (item._tlvType)
                {
                    case 0x01: // EPC
                        tagData.Epc = BitConverter.ToString(item._tlvValue).Replace("-", "");
                        break;
                    case 0x02: // User Data
                        tagData.UserData = item._tlvValue;
                        break;
                    case 0x04: // TID
                        tagData.TidData = item._tlvValue;
                        break;
                    case 0x05: // RSSI
                        if (item._tlvValue.Length > 0)
                            tagData.Rssi = item._tlvValue[0];
                        break;
                    case 0x0A: // Antenna No
                        if (item._tlvValue.Length > 0)
                            tagData.AntennaNo = item._tlvValue[0];
                        break;
                }
            }

            Log.Debug("接收到Frid标签数据: EPC={Epc}, Antenna={Antenna}, RSSI={Rssi}", 
                tagData.Epc, tagData.AntennaNo, tagData.Rssi);

            TagDataReceived?.Invoke(tagData);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理Frid标签数据时发生错误");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            DisconnectAsync().Wait();
            _reader?.ReleaseResource();
            _reader = null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "释放Frid设备资源时发生错误");
        }

        _disposed = true;
    }
}

/// <summary>
/// Frid Reader通知实现
/// </summary>
internal class FridReaderNotifyImpl(FridService fridService) : RfidReaderRspNotify
{
    public void OnRecvResetRsp(RfidReader reader, byte result)
    {
        Log.Information("Frid设备重置响应: {Result}", result);
    }

    public void OnRecvSetFactorySettingRsp(RfidReader reader, byte result)
    {
        Log.Information("Frid设备恢复出厂设置响应: {Result}", result);
    }

    public void OnRecvStartInventoryRsp(RfidReader reader, byte result)
    {
        Log.Information("Frid设备开始盘点响应: {Result}", result);
    }

    public void OnRecvStopInventoryRsp(RfidReader reader, byte result)
    {
        Log.Information("Frid设备停止盘点响应: {Result}", result);
    }

    public void OnRecvDeviceInfoRsp(RfidReader reader, byte[] firmwareVersion, byte deviceType)
    {
        Log.Information("Frid设备信息响应: 固件版本={FirmwareVersion}, 设备类型={DeviceType}", 
            BitConverter.ToString(firmwareVersion), deviceType);
    }

    public void OnRecvSetWorkParamRsp(RfidReader reader, byte result)
    {
        Log.Information("Frid设备设置工作参数响应: {Result}", result);
    }

    public void OnRecvQueryWorkParamRsp(RfidReader reader, byte result, RfidWorkParam workParam)
    {
        Log.Information("Frid设备查询工作参数响应: {Result}, 功率={Power}", result, workParam.ucRFPower);
    }

    public void OnRecvSetTransmissionParamRsp(RfidReader reader, byte result)
    {
        Log.Information("Frid设备设置传输参数响应: {Result}", result);
    }

    public void OnRecvQueryTransmissionParamRsp(RfidReader reader, byte result, RfidTransmissionParam transmissiomParam)
    {
        Log.Information("Frid设备查询传输参数响应: {Result}", result);
    }

    public void OnRecvSetAdvanceParamRsp(RfidReader reader, byte result)
    {
        Log.Information("Frid设备设置高级参数响应: {Result}", result);
    }

    public void OnRecvQueryAdvanceParamRsp(RfidReader reader, byte result, RfidAdvanceParam advanceParam)
    {
        Log.Information("Frid设备查询高级参数响应: {Result}", result);
    }

    public void OnRecvTagNotify(RfidReader reader, TlvValueItem[] tlvItems, byte tlvCount)
    {
        fridService.OnTagDataReceived(tlvItems, tlvCount);
    }

    public void OnRecvHeartBeats(RfidReader reader, TlvValueItem[] tlvItems, byte tlvCount)
    {
        Log.Debug("Frid设备心跳响应");
    }

    public void OnRecvSettingSingleParam(RfidReader reader, byte result)
    {
        Log.Information("Frid设备设置单个参数响应: {Result}", result);
    }

    public void OnRecvQuerySingleParam(RfidReader reader, TlvValueItem item)
    {
        Log.Information("Frid设备查询单个参数响应: 类型={Type}, 长度={Length}", 
            item._tlvType, item._tlvLen);
    }

    public void OnRecvWriteTagRsp(RfidReader reader, byte result)
    {
        Log.Information("Frid设备写标签响应: {Result}", result);
    }

    public void OnRecvRecordNotify(RfidReader reader, string time, string tagId)
    {
        Log.Information("Frid设备记录通知: 时间={Time}, 标签ID={TagId}", time, tagId);
    }

    public void OnRecvRecordStatusRsp(RfidReader reader, byte result)
    {
        Log.Information("Frid设备记录状态响应: {Result}", result);
    }

    public void OnRecvSetRtcTimeStatusRsp(RfidReader reader, byte result)
    {
        Log.Information("Frid设备设置RTC时间响应: {Result}", result);
    }

    public void OnRecvQueryRtcTimeRsp(int year, int month, int day, int hour, int min, int sec)
    {
        Log.Information("Frid设备查询RTC时间响应: {Year}-{Month}-{Day} {Hour}:{Min}:{Sec}", 
            year, month, day, hour, min, sec);
    }

    public void OnRecvReadBlockRsp(RfidReader reader, byte result, byte[] readData, byte[] epcData)
    {
        Log.Information("Frid设备读块响应: {Result}, 数据长度={DataLength}", result, readData.Length);
    }

    public void OnRecvWriteWiegandNumberRsp(RfidReader reader, byte result)
    {
        Log.Information("Frid设备写Wiegand号码响应: {Result}", result);
    }

    public void OnRecvLockResult(RfidReader reader, byte result)
    {
        Log.Information("Frid设备锁定标签响应: {Result}", result);
    }

    public void OnRecvQueryExtParamRsp(RfidReader reader, byte result, RfidExtParam extParam)
    {
        Log.Information("Frid设备查询扩展参数响应: {Result}", result);
    }

    public void OnRecvSetExtParam(RfidReader reader, byte result)
    {
        Log.Information("Frid设备设置扩展参数响应: {Result}", result);
    }

    public void OnRecvAudioPlayRsp(RfidReader reader, byte result)
    {
        Log.Information("Frid设备音频播放响应: {Result}", result);
    }

    public void OnRecvRelayOpRsp(RfidReader reader, byte result)
    {
        Log.Information("Frid设备继电器操作响应: {Result}", result);
    }

    public void OnRecvVerifyTagRsp(RfidReader reader, byte result)
    {
        Log.Information("Frid设备验证标签响应: {Result}", result);
    }

    public void OnRecvSetUsbInfoRsp(RfidReader reader, byte result)
    {
        Log.Information("Frid设备设置USB信息响应: {Result}", result);
    }

    public void OnRecvQueryUsbInfoRsp(RfidReader reader, byte interfaceType, byte usbProto, byte enterflag)
    {
        Log.Information("Frid设备查询USB信息响应: 接口类型={InterfaceType}, USB协议={UsbProto}, 进入标志={EnterFlag}", 
            interfaceType, usbProto, enterflag);
    }

    public void OnRecvSetDataFlagRsp(RfidReader reader, byte result)
    {
        Log.Information("Frid设备设置数据标志响应: {Result}", result);
    }

    public void OnRecvQueryDataFlagRsp(RfidReader reader, ushort dataflag, byte dataformat)
    {
        Log.Information("Frid设备查询数据标志响应: 数据标志={DataFlag}, 数据格式={DataFormat}", 
            dataflag, dataformat);
    }

    public void OnRecvQueryModbusParam(RfidReader reader, byte tagNum, byte unionSize, byte startaddr, byte clearflag, int modbusProto)
    {
        Log.Information("Frid设备查询Modbus参数响应: 标签数量={TagNum}, 联合大小={UnionSize}, 起始地址={StartAddr}, 清除标志={ClearFlag}, Modbus协议={ModbusProto}", 
            tagNum, unionSize, startaddr, clearflag, modbusProto);
    }

    public void OnRecvSetModbusParamRsp(RfidReader reader, byte result)
    {
        Log.Information("Frid设备设置Modbus参数响应: {Result}", result);
    }

    public void OnRecvTagData(RfidReader reader, TlvValueItem[] tlvItems, byte tlvCount)
    {
        fridService.OnTagDataReceived(tlvItems, tlvCount);
    }
} 
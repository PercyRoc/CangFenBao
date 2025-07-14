using System.IO.Ports;
using Serilog;

namespace CangFenBao.SDK
{
    /// <summary>
    /// 直接串口分拣服务，仅负责发送外部传入的命令。
    /// </summary>
    internal class DirectSorterService : IAsyncDisposable
    {
        private SerialPort? _serialPort;
        public event Action<bool>? ConnectionChanged;

        public DirectSorterService(string portName, int baudRate, int dataBits, int stopBits, int parity, int readTimeout, int writeTimeout)
        {
            _serialPort = new SerialPort
            {
                PortName = portName,
                BaudRate = baudRate,
                DataBits = dataBits,
                StopBits = (StopBits)stopBits,
                Parity = (Parity)parity,
                ReadTimeout = readTimeout,
                WriteTimeout = writeTimeout
            };
        }

        public Task StartAsync()
        {
            try
            {
                if (_serialPort != null && !_serialPort.IsOpen)
                {
                    _serialPort.Open();
                    Log.Information("串口 {PortName} 已打开。", _serialPort.PortName);
                    ConnectionChanged?.Invoke(true);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "打开串口 {PortName} 失败。", _serialPort?.PortName);
                ConnectionChanged?.Invoke(false);
            }
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.Close();
                Log.Information("串口 {PortName} 已关闭。", _serialPort.PortName);
                ConnectionChanged?.Invoke(false);
            }
            return Task.CompletedTask;
        }

        public async Task<bool> SendCommandAsync(byte[] command)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                Log.Warning("无法发送指令：串口未连接。");
                return false;
            }
            try
            {
                await _serialPort.BaseStream.WriteAsync(command, 0, command.Length);
                Log.Information("指令发送成功: {CommandHex}", BitConverter.ToString(command));
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "发送串口指令失败: {CommandHex}", BitConverter.ToString(command));
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _serialPort?.Dispose();
        }
    }
} 
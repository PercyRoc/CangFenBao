using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Serilog;
using Timer = System.Timers.Timer;

namespace DeviceService.DataSourceDevices.Scanner;

/// <summary>
///     USB扫码枪服务实现
/// </summary>
internal class UsbScannerService : IScannerService
{
    private const int ScannerTimeout = 100; // 扫码枪输入超时时间 (增加到 500ms)
    private readonly StringBuilder _barcodeBuilder = new();
    private readonly LowLevelKeyboardProc _proc;
    private readonly Queue<string> _barcodeQueue = new();
    private readonly object _lock = new();
    private readonly Timer _processTimer;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _isRunning;
    private DateTime _lastKeyTime = DateTime.MinValue;
    
    // 添加最后处理条码的记录
    private string _lastBarcode = string.Empty;
    private DateTime _lastBarcodeTime = DateTime.MinValue;
    private const int DuplicateBarcodeIntervalMs = 3000; // 3秒内不重复处理相同条码

    public UsbScannerService()
    {
        _proc = HookCallback;
        
        // 初始化处理定时器
        _processTimer = new Timer(100); // 每100ms处理一次
        _processTimer.Elapsed += ProcessBarcodeQueue;
        _processTimer.Start();
    }

    public event EventHandler<string>? BarcodeScanned;

    public bool Start()
    {
        try
        {
            if (_isRunning) return true;

            _hookId = SetHook(_proc);
            if (_hookId == IntPtr.Zero)
            {
                Log.Error("设置键盘钩子失败");
                return false;
            }

            _isRunning = true;
            Log.Information("USB扫码枪服务已启动");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动USB扫码枪服务失败");
            return false;
        }
    }

    public void Stop()
    {
        if (!_isRunning) return;

        try
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }

            _isRunning = false;
            _barcodeBuilder.Clear();
            _barcodeQueue.Clear();
            Log.Information("USB扫码枪服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止USB扫码枪服务时发生错误");
        }
    }

    public void Dispose()
    {
        Stop();
        _processTimer.Dispose();
        GC.SuppressFinalize(this);
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule ?? throw new InvalidOperationException("无法获取当前进程主模块");
        return SetWindowsHookEx(13, proc, GetModuleHandle(null), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || wParam != 0x0100) return CallNextHookEx(_hookId, nCode, wParam, lParam); // WM_KEYDOWN

        var vkCode = Marshal.ReadInt32(lParam);
        var currentTime = DateTime.Now;

        // 检查是否超时，如果超时则清空缓存
        if ((currentTime - _lastKeyTime).TotalMilliseconds > ScannerTimeout && _barcodeBuilder.Length > 0)
        {
            Log.Debug("扫码超时，清空缓存");
            _barcodeBuilder.Clear();
        }

        _lastKeyTime = currentTime;

        // 处理按键输入
        if (vkCode == 13) // 回车键
        {
            if (_barcodeBuilder.Length <= 0) return CallNextHookEx(_hookId, nCode, wParam, lParam);

            var barcode = _barcodeBuilder.ToString();
            _barcodeBuilder.Clear();
            
            // 验证条码
            if (ValidateBarcode(barcode))
            {
                // 将条码添加到队列
                lock (_lock)
                {
                    _barcodeQueue.Enqueue(barcode);
                }
                Log.Debug("收到有效条码：{Barcode}", barcode);
            }
            else
            {
                Log.Warning("收到无效条码：{Barcode}", barcode);
            }
        }
        else
        {
            var key = (Keys)vkCode;
            if (char.IsLetterOrDigit((char)key) || key == Keys.OemMinus) _barcodeBuilder.Append((char)key);
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void ProcessBarcodeQueue(object? sender, System.Timers.ElapsedEventArgs e)
    {
        lock (_lock)
        {
            while (_barcodeQueue.Count > 0)
            {
                var barcode = _barcodeQueue.Dequeue();
                try
                {
                    // 检查是否是在短时间内的重复条码
                    var now = DateTime.Now;
                    if (barcode == _lastBarcode && 
                        (now - _lastBarcodeTime).TotalMilliseconds < DuplicateBarcodeIntervalMs)
                    {
                        Log.Warning("忽略重复条码：{Barcode}，间隔仅 {Interval} 毫秒", 
                            barcode, (now - _lastBarcodeTime).TotalMilliseconds);
                        continue;
                    }
                    
                    // 更新最后处理的条码记录
                    _lastBarcode = barcode;
                    _lastBarcodeTime = now;
                    
                    BarcodeScanned?.Invoke(this, barcode);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理条码事件时发生错误：{Barcode}", barcode);
                }
            }
        }
    }

    private static bool ValidateBarcode(string barcode)
    {
        // 添加条码格式验证
        if (string.IsNullOrEmpty(barcode) || barcode.Length < 5)
        {
            return false;
        }
        return true;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    #region Native Methods

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    #endregion
}
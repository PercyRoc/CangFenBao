using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;
using Timer = System.Timers.Timer;

namespace DeviceService.DataSourceDevices.Scanner;

/// <summary>
///     USB扫码枪服务实现
/// </summary>
internal class UsbScannerService : IScannerService
{
    private const int ScannerTimeout = 200;
    private readonly StringBuilder _barcodeBuilder = new();
    private readonly LowLevelKeyboardProc _proc;
    private readonly Queue<string> _barcodeQueue = new();
    private readonly object _lock = new();
    private readonly Timer _processTimer;
    private readonly Subject<string> _barcodeSubject = new();
    private IntPtr _hookId = IntPtr.Zero;
    private bool _isRunning;
    private DateTime _lastKeyTime = DateTime.MinValue;
    
    // 添加最后处理条码的记录
    private string _lastBarcode = string.Empty;
    private DateTime _lastBarcodeTime = DateTime.MinValue;
    private const int DuplicateBarcodeIntervalMs = 3000; // 3秒内不重复处理相同条码
    
    // 添加拦截控制开关
    private bool _interceptAllInput = true; // 默认拦截所有扫码枪输入
    
    // WinAPI Constants
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const short VK_SHIFT = 0x10;
    private const short VK_RETURN = 0x0D; // Enter key
    private const short VK_OEM_MINUS = 0xBD; // '-' on main keyboard
    private const short VK_SUBTRACT = 0x6D; // '-' on numpad
    private const short VK_OEM_2 = 0xBF; // '/?' on main keyboard
    private const short VK_DIVIDE = 0x6F; // '/' on numpad
    
    /// <summary>
    /// 获取或设置是否拦截所有扫码枪输入，防止字符进入输入框
    /// </summary>
    public bool InterceptAllInput
    {
        get => _interceptAllInput;
        set => _interceptAllInput = value;
    }

    public UsbScannerService()
    {
        _proc = HookCallback;
        
        // 初始化处理定时器
        _processTimer = new Timer(100); // 每100ms处理一次
        _processTimer.Elapsed += ProcessBarcodeQueue;
    }

    public IObservable<string> BarcodeStream => _barcodeSubject.AsObservable();

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

            _processTimer.Start();
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
            _processTimer.Stop();
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
        _barcodeSubject.Dispose();
        GC.SuppressFinalize(this);
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule ?? throw new InvalidOperationException("无法获取当前进程主模块");
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hookId, nCode, wParam, lParam);
        
        if (wParam != WM_KEYDOWN) return CallNextHookEx(_hookId, nCode, wParam, lParam);

        var vkCode = Marshal.ReadInt32(lParam);
        var currentTime = DateTime.Now;
        bool isLikelyScanner = false;

        if ((currentTime - _lastKeyTime).TotalMilliseconds > ScannerTimeout && _barcodeBuilder.Length > 0)
        {
            Log.Debug("扫码超时，清空缓存");
            _barcodeBuilder.Clear();
        }
        else if (_barcodeBuilder.Length > 0)
        {
            isLikelyScanner = (currentTime - _lastKeyTime).TotalMilliseconds < ScannerTimeout;
        }
        _lastKeyTime = currentTime;

        char charToAdd = '\0';
        bool isValidBarcodeChar = false;
        bool isEnterKey = false;

        // --- Check Shift Key State ---
        bool shiftPressed = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
        Log.Verbose("VKCode: {VKCode}, Shift Pressed: {ShiftPressed}", vkCode, shiftPressed);

        // --- Process Key based on VK Code ---
        switch (vkCode)
        {
            case VK_RETURN: // Enter
                isEnterKey = true;
                break;

            case VK_OEM_MINUS: // '-' or '_' on main keyboard
            case VK_SUBTRACT: // '-' on numpad
                isValidBarcodeChar = true;
                charToAdd = shiftPressed ? '_' : '-';
                break;

            case VK_OEM_2: // '/' or '?' on main keyboard
                isValidBarcodeChar = true;
                charToAdd = shiftPressed ? '?' : '/'; 
                break;
            
            case VK_DIVIDE: // '/' on numpad
                isValidBarcodeChar = true;
                charToAdd = '/';
                break;
                
            // 可以根据需要添加其他特殊字符的 VK Code 映射

            default:
                if (vkCode >= 0x30 && vkCode <= 0x39) // 0-9
                {
                    isValidBarcodeChar = true;
                    if (!shiftPressed) 
                    {
                        charToAdd = (char)vkCode; // '0' through '9'
                    }
                    else
                    {
                        charToAdd = vkCode switch {
                            0x30 => ')', // Shift + 0
                            0x31 => '!', // Shift + 1
                            0x32 => '@', // Shift + 2
                            0x33 => '#', // Shift + 3
                            0x34 => '$', // Shift + 4
                            0x35 => '%', // Shift + 5
                            0x36 => '^', // Shift + 6
                            0x37 => '&', // Shift + 7
                            0x38 => '*', // Shift + 8
                            0x39 => '(', // Shift + 9
                            _ => '\0' // Should not happen
                        };
                        if (charToAdd == '\0') isValidBarcodeChar = false;
                    }
                }
                else if (vkCode >= 0x41 && vkCode <= 0x5A) // A-Z
                {
                    isValidBarcodeChar = true;
                    charToAdd = (char)vkCode; 
                }
                else if (vkCode >= 32 && vkCode <= 126 && !char.IsLetterOrDigit((char)vkCode) && vkCode != VK_OEM_MINUS && vkCode != VK_OEM_2) 
                {
                    isValidBarcodeChar = true;
                    charToAdd = (char)vkCode;
                    Log.Debug("Fallback handling for VKCode: {VKCode} -> Char: {Char}", vkCode, charToAdd);
                }
                else
                {
                    Log.Verbose("Unhandled VK Code: {VKCode}", vkCode);
                }
                break;
        }

        if (isEnterKey)
        {
            if (_barcodeBuilder.Length <= 0) return CallNextHookEx(_hookId, nCode, wParam, lParam);

            var barcode = _barcodeBuilder.ToString();
            _barcodeBuilder.Clear();
            
            Log.Debug("收到可能的条码：{Barcode}, 长度: {Length}", barcode, barcode.Length);
            
            if (ValidateBarcode(barcode))
            {
                lock (_lock)
                {
                    _barcodeQueue.Enqueue(barcode);
                }
                Log.Debug("收到有效条码：{Barcode}", barcode);
                return 1;
            }
            else
            {
                Log.Warning("收到无效条码：{Barcode}，长度: {Length}", barcode, barcode.Length);
                if (barcode.Length >= 3)
                {
                    return 1;
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }
        else if (isValidBarcodeChar)
        {
            _barcodeBuilder.Append(charToAdd);
            Log.Debug("添加字符 '{0}' (VKCode: {1}, Shift: {2}) 到条码缓冲区", charToAdd, vkCode, shiftPressed);
            
            bool shouldIntercept = (_interceptAllInput && isValidBarcodeChar) || 
                                   isLikelyScanner || 
                                   _barcodeBuilder.Length >= 2;
            
            if (shouldIntercept)
            {
                Log.Debug("拦截按键: {0}, 条码缓冲区长度: {1}, 拦截模式: {2}", 
                    charToAdd, _barcodeBuilder.Length, 
                    _interceptAllInput ? "全局拦截" : "智能检测");
                return 1;
            }
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
                    var now = DateTime.Now;
                    if (barcode == _lastBarcode && 
                        (now - _lastBarcodeTime).TotalMilliseconds < DuplicateBarcodeIntervalMs)
                    {
                        Log.Warning("忽略重复条码：{Barcode}，间隔仅 {Interval} 毫秒", 
                            barcode, (now - _lastBarcodeTime).TotalMilliseconds);
                        continue;
                    }
                    
                    _lastBarcode = barcode;
                    _lastBarcodeTime = now;
                    
                    _barcodeSubject.OnNext(barcode);
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
        return !string.IsNullOrEmpty(barcode) && barcode.Length >= 3;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    #region Native Methods

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall)]
    private static extern short GetKeyState(int nVirtKey);

    #endregion
}
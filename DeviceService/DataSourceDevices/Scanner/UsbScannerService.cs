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
    // 扫描枪输入超时时间 (毫秒)
    private const int ScannerTimeout = 200;
    private readonly StringBuilder _barcodeBuilder = new(); // 用于构建条码字符串
    private readonly LowLevelKeyboardProc _proc; // 低级键盘钩子回调函数
    private readonly Queue<string> _barcodeQueue = new(); // 条码处理队列
    private readonly object _lock = new(); // 队列锁
    private readonly Timer _processTimer; // 条码队列处理定时器
    private readonly Subject<string> _barcodeSubject = new(); // 用于发布条码的 Reactive Subject
    private IntPtr _hookId = IntPtr.Zero; // 键盘钩子句柄
    private bool _isRunning; // 服务运行状态标志
    private DateTime _lastKeyTime = DateTime.MinValue; // 上次按键时间
    private bool _isShiftPressed; // 新增：追踪Shift键状态

    // 添加最后处理条码的记录
    private string _lastBarcode = string.Empty; // 上一个成功处理的条码
    private DateTime _lastBarcodeTime = DateTime.MinValue; // 上一个条码的处理时间
    private const int DuplicateBarcodeIntervalMs = 3000; // 重复条码过滤时间间隔 (毫秒)

    // 添加拦截控制开关
    private bool _interceptAllInput = true; // 控制是否拦截所有扫码枪输入 (防止字符进入输入框)

    // WinAPI 常量
    private const int WhKeyboardLl = 13; // 低级键盘钩子类型
    private const int WmKeydown = 0x0100; // 键盘按下消息
    private const int WmKeyup = 0x0101; // 键盘弹起消息
    private const short VkShift = 0x10; // Shift 虚拟键码 (通用)
    private const short VkLshift = 0xA0; // 左 Shift 虚拟键码
    private const short VkRshift = 0xA1; // 右 Shift 虚拟键码
    private const short VkReturn = 0x0D; // Enter 虚拟键码
    private const short VkOemMinus = 0xBD; // OEM_MINUS 虚拟键码


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
        _proc = HookCallback; // 设置钩子回调

        // 初始化处理定时器
        _processTimer = new Timer(100); // 每100毫秒处理一次队列
        _processTimer.Elapsed += ProcessBarcodeQueue;
    }

    /// <summary>
    /// 提供条码事件流
    /// </summary>
    public IObservable<string> BarcodeStream => _barcodeSubject.AsObservable();

    /// <summary>
    /// 启动扫码枪服务
    /// </summary>
    /// <returns>启动是否成功</returns>
    public bool Start()
    {
        try
        {
            if (_isRunning) return true; // 如果已运行，直接返回

            _hookId = SetHook(_proc); // 设置键盘钩子
            if (_hookId == IntPtr.Zero)
            {
                Log.Error("设置键盘钩子失败");
                return false;
            }

            _processTimer.Start(); // 启动队列处理定时器
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

    /// <summary>
    /// 停止扫码枪服务
    /// </summary>
    public void Stop()
    {
        if (!_isRunning) return; // 如果未运行，直接返回

        try
        {
            _processTimer.Stop(); // 停止队列处理定时器
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId); // 卸载键盘钩子
                _hookId = IntPtr.Zero;
            }

            _isRunning = false;
            _barcodeBuilder.Clear(); // 清空条码缓冲区
            _barcodeQueue.Clear(); // 清空条码队列
            Log.Information("USB扫码枪服务已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止USB扫码枪服务时发生错误");
        }
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        Stop(); // 停止服务
        _processTimer.Dispose(); // 释放定时器资源
        _barcodeSubject.Dispose(); // 释放 Subject
        GC.SuppressFinalize(this); // 阻止垃圾回收器调用终结器
    }

    /// <summary>
    /// 设置 Windows 钩子
    /// </summary>
    /// <param name="proc">钩子回调函数</param>
    /// <returns>钩子句柄，失败则返回 IntPtr.Zero</returns>
    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule ?? throw new InvalidOperationException("无法获取当前进程主模块");
        // 使用 GetModuleHandle(null) 获取当前应用程序实例的句柄，适用于全局钩子
        return SetWindowsHookEx(WhKeyboardLl, proc, GetModuleHandle(null), 0);
    }

    /// <summary>
    /// 低级键盘钩子回调函数
    /// </summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hookId, nCode, wParam, lParam);

        var hookStruct = Marshal.PtrToStructure<Kbdllhookstruct>(lParam);
        var vkCode = hookStruct.vkCode;
        var scanCode = hookStruct.scanCode;
        var currentTime = DateTime.Now;

        // --- 添加全局按键日志 ---
        string messageType = wParam == WmKeydown ? "KeyDown" : (wParam == WmKeyup ? "KeyUp" : $"Other({wParam})");
        Log.Debug("[全局日志] 收到按键事件: 类型={MsgType}, VKCode={VKCode} (十六进制: {VKCodeHex}), ScanCode={ScanCode}",
            messageType, vkCode, $"0x{vkCode:X2}", scanCode);
        // --- 全局日志结束 ---

        // --- 扩展 Shift 状态追踪 (包含左右 Shift) --- 
        if (vkCode == VkShift || vkCode == VkLshift || vkCode == VkRshift)
        {
            bool previousShiftState = _isShiftPressed;
            if (wParam == WmKeydown)
            {
                _isShiftPressed = true;
                if (!previousShiftState) Log.Debug("Shift 键按下 (追踪)。VKCode={VKCode}", vkCode);
            }
            else if (wParam == WmKeyup)
            {
                _isShiftPressed = false;
                if (previousShiftState) Log.Debug("Shift 键弹起 (追踪)。VKCode={VKCode}", vkCode);
            }

            // Shift 键事件本身直接传递
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // --- 处理字符按键的 KeyDown 事件 (后续逻辑不变) ---
        if (wParam == WmKeydown)
        {
            // --- 超时检查 (逻辑不变) ---
            var timeDiff = (currentTime - _lastKeyTime).TotalMilliseconds;
            if (timeDiff >= ScannerTimeout && _barcodeBuilder.Length > 0)
            {
                Log.Debug("输入超时 ({TimeDiff}ms)...", timeDiff);
                _barcodeBuilder.Clear();
            }

            _lastKeyTime = currentTime;

            // --- 处理回车键 (逻辑不变) ---
            if (vkCode == VkReturn)
            {
                if (_barcodeBuilder.Length <= 0) return CallNextHookEx(_hookId, nCode, wParam, lParam);
                var barcode = _barcodeBuilder.ToString();
                _barcodeBuilder.Clear();
                Log.Debug("收到可能的条码（回车触发）：{Barcode}, 长度: {Length}", barcode, barcode.Length);
                if (ValidateBarcode(barcode))
                {
                    lock (_lock)
                    {
                        _barcodeQueue.Enqueue(barcode);
                    }

                    Log.Debug("条码有效，已入队：{Barcode}", barcode);
                    return 1;
                }
                else
                {
                    Log.Warning("条码无效（回车触发）：{Barcode}，长度: {Length}", barcode, barcode.Length);
                    if (barcode.Length >= 3)
                    {
                        return 1;
                    }
                }

                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            // --- 处理字符按键 ---
            var keyboardState = new byte[256];
            if (!GetKeyboardState(keyboardState))
            {
                Log.Warning("GetKeyboardState 调用失败。VKCode={VKCode}", vkCode);
                return _interceptAllInput ? 1 : CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            // *** 使用追踪的 Shift 状态修正 keyboardState ***
            bool originalShiftStateGks = (keyboardState[VkShift] & 0x80) != 0; // 仅用于日志比较
            if (_isShiftPressed)
            {
                keyboardState[VkShift] |= 0x80; // 强制 Shift 按下
                if (!originalShiftStateGks) Log.Debug("强制 Shift 为按下状态 (基于追踪)。VKCode={VKCode}", vkCode);
            }
            else
            {
                keyboardState[VkShift] &= 0x7F; // 强制 Shift 弹起
                if (originalShiftStateGks) Log.Debug("强制 Shift 为弹起状态 (基于追踪)。VKCode={VKCode}", vkCode);
            }
            // *** 修正结束 ***

            // 记录最终使用的 Shift 状态
            bool shiftStateToUse = (keyboardState[VkShift] & 0x80) != 0;
            bool capsState = (keyboardState[0x14 /*VK_CAPITAL*/] & 0x01) != 0;
            Log.Debug("准备调用 ToUnicodeEx: Shift={ShiftState}, Caps={CapsState}, VKCode={VKCode}", shiftStateToUse,
                capsState, vkCode);

            // --- 详细日志 for VK_OEM_MINUS (保持) ---
            if (vkCode == VkOemMinus)
            {
                Log.Debug("[VK_OEM_MINUS 详细日志] 调用 ToUnicodeEx 之前的状态: Shift (基于追踪)={ShiftCorrected}, Caps={CapsState}",
                    shiftStateToUse, capsState);
            }
            // --- 详细日志结束 ---

            // 数字键 Shift 强制UP (逻辑保留)
            if (vkCode is >= 0x30 and <= 0x39 && shiftStateToUse)
            {
                Log.Debug("覆盖数字键 VKCode {VKCode} 的 Shift 状态为 UP", vkCode);
                keyboardState[VkShift] = 0; // 清除 Shift 位
                shiftStateToUse = false; // 更新日志用状态
            }

            // 大写字母强制 (逻辑保留)
            bool forceUppercase = vkCode is >= 0x41 and <= 0x5A;

            var hkl = GetKeyboardLayout(0);
            var sb = new StringBuilder(2);
            var result = ToUnicodeEx((uint)vkCode, scanCode, keyboardState, sb, sb.Capacity, 0, hkl);

            // --- 详细日志 for VK_OEM_MINUS (保持) ---
            if (vkCode == VkOemMinus)
            {
                string producedChars = (result > 0) ? sb.ToString(0, result) : "[无]";
                Log.Debug(
                    "[VK_OEM_MINUS 详细日志] ToUnicodeEx 结果: 产生字符='{Produced}', 返回码={ResultCode}, 使用的Shift状态={UsedShift}",
                    producedChars, result, shiftStateToUse);
            }
            // --- 详细日志结束 ---

            if (result > 0)
            {
                char charFromToUnicodeEx = sb[0];
                char charToAdd = charFromToUnicodeEx;
                if (forceUppercase)
                {
                    char forcedUpperChar = (char)vkCode;
                    if (charToAdd != forcedUpperChar)
                    {
                        Log.Warning("强制将 VKCode {VKCode} 的 ToUnicodeEx 结果 '{OriginalChar}' 改为大写 '{ForcedChar}'", vkCode,
                            charFromToUnicodeEx, forcedUpperChar);
                        charToAdd = forcedUpperChar;
                    }
                }

                _barcodeBuilder.Append(charToAdd);
                Log.Debug("添加字符 '{Char}' (VKCode: {VKCode}) 到缓冲区. 当前模式: {Mode}",
                    charToAdd, vkCode, _interceptAllInput ? "扫描枪(拦截)" : "手动(不拦截)");

                if (_interceptAllInput)
                {
                    Log.Debug("拦截按键 (扫描枪模式): '{Char}'", charToAdd);
                    return 1;
                }
                else
                {
                    Log.Debug("不拦截按键 (手动模式): '{Char}'", charToAdd);
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
                }
            }
            else // ToUnicodeEx 转换失败
            {
                Log.Debug("ToUnicodeEx 转换失败或未产生字符 (VKCode: {VKCode}, Result: {Result})", vkCode, result);
                if (_interceptAllInput)
                {
                    Log.Debug("拦截按键 (扫描枪模式, ToUnicodeEx失败): VKCode {VKCode}", vkCode);
                    return 1;
                }
                else
                {
                    Log.Debug("不拦截按键 (手动模式, ToUnicodeEx失败): VKCode {VKCode}", vkCode);
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
                }
            }
        } // 结束 if (wParam == WM_KEYDOWN)
        // --- 处理非 Shift 键的 KeyUp 事件 ---
        else if (wParam == WmKeyup)
        {
            Log.Debug("传递非 Shift KeyUp 事件: VKCode={VKCode}", vkCode);
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // --- 默认传递其他所有消息 ---
        Log.Debug("[全局日志] 传递未知或未处理的消息: 类型={MsgType}, VKCode={VKCode}", messageType, vkCode);
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>
    /// 定时处理条码队列
    /// </summary>
    private void ProcessBarcodeQueue(object? sender, System.Timers.ElapsedEventArgs e)
    {
        lock (_lock) // 加锁访问队列
        {
            while (_barcodeQueue.Count > 0) // 只要队列不为空
            {
                var barcode = _barcodeQueue.Dequeue(); // 取出条码
                try
                {
                    var now = DateTime.Now;
                    // 检查是否为重复条码 (在指定时间间隔内)
                    if (barcode == _lastBarcode &&
                        (now - _lastBarcodeTime).TotalMilliseconds < DuplicateBarcodeIntervalMs)
                    {
                        Log.Warning("忽略重复条码：{Barcode}，间隔仅 {Interval} 毫秒",
                            barcode, (now - _lastBarcodeTime).TotalMilliseconds);
                        continue; // 跳过处理
                    }

                    // 更新最后处理的条码和时间
                    _lastBarcode = barcode;
                    _lastBarcodeTime = now;

                    // 通过 Subject 发布条码
                    _barcodeSubject.OnNext(barcode);
                }
                catch (Exception ex)
                {
                    // 记录处理条码事件时发生的异常
                    Log.Error(ex, "处理条码事件时发生错误：{Barcode}", barcode);
                }
            }
        }
    }

    /// <summary>
    /// 验证条码的基本有效性 (非空且长度大于等于3)
    /// </summary>
    /// <param name="barcode">待验证的条码</param>
    /// <returns>是否有效</returns>
    private static bool ValidateBarcode(string barcode)
    {
        // 可根据实际业务需求扩展更复杂的验证逻辑
        return !string.IsNullOrEmpty(barcode) && barcode.Length >= 3;
    }

    /// <summary>
    /// 低级键盘钩子过程委托
    /// </summary>
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    #region Native Methods (WinAPI P/Invoke) - WinAPI 函数导入

    /// <summary>
    /// 低级键盘输入事件结构
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Kbdllhookstruct
    {
        public int vkCode; // 虚拟键码
        public uint scanCode; // 硬件扫描码
        public uint flags; // 标志 (例如 LLKHF_EXTENDED)
        public uint time; // 事件时间戳
        public IntPtr dwExtraInfo; // 额外信息
    }

    /// <summary>
    /// 安装一个应用程序定义的钩子过程到一个钩子链。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    /// <summary>
    /// 移除一个安装在系统钩子链中的钩子过程。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    /// <summary>
    /// 将钩子信息传递到当前钩子链中的下一个钩子过程。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 检索指定模块的模块句柄。模块必须已被当前进程加载。
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName); // 传递 null 获取当前应用程序的句柄

    // GetKeyState 已移除，不再用于字符转换

    // 添加了 ToUnicodeEx 必需的函数
    /// <summary>
    /// 将虚拟键码和键盘状态翻译成相应的 Unicode 字符。
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState(byte[] lpKeyState); // 获取当前键盘状态

    /// <summary>
    /// 检索活动线程的输入区域设置标识符（以前称为键盘布局）。
    /// </summary>
    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread); // 传递 0 获取当前线程的布局

    /// <summary>
    /// 将指定的虚拟键代码和键盘状态转换为相应的一个或多个 Unicode 字符。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] // 使用 Unicode 字符集以配合 StringBuilder
    private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out, MarshalAs(UnmanagedType.LPWStr, SizeConst = 4)]
        StringBuilder pwszBuff, // 使用 StringBuilder 接收输出
        int cchBuff, uint wFlags, IntPtr dwhkl); // dwhkl: 要使用的键盘布局句柄

    // 新增 GetAsyncKeyState

    #endregion
}
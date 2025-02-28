using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Serilog;

namespace DeviceService.Scanner;

/// <summary>
///     USB扫码枪服务实现
/// </summary>
public class UsbScannerService : IScannerService
{
    private readonly StringBuilder _barcodeBuilder = new();
    private readonly LowLevelKeyboardProc _proc;
    private readonly int _scannerTimeout = 50; // 扫码枪输入超时时间（毫秒）
    private IntPtr _hookId = IntPtr.Zero;
    private bool _isRunning;
    private DateTime _lastKeyTime = DateTime.MinValue;

    public UsbScannerService()
    {
        _proc = HookCallback;
    }

    public event EventHandler<string>? BarcodeScanned;

    public bool Start()
    {
        try
        {
            if (_isRunning) return true;

            _hookId = SetHook(_proc);
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

        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        _isRunning = false;
        Log.Information("USB扫码枪服务已停止");
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        if (curModule == null) throw new InvalidOperationException("无法获取当前进程主模块");
        return SetWindowsHookEx(13, proc, GetModuleHandle(curModule.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || wParam != 0x0100) return CallNextHookEx(_hookId, nCode, wParam, lParam); // WM_KEYDOWN
        var vkCode = Marshal.ReadInt32(lParam);
        var currentTime = DateTime.Now;

        // 检查是否超时，如果超时则清空缓存
        if ((currentTime - _lastKeyTime).TotalMilliseconds > _scannerTimeout && _barcodeBuilder.Length > 0)
            _barcodeBuilder.Clear();

        _lastKeyTime = currentTime;

        // 处理按键输入
        if (vkCode == 13) // 回车键
        {
            if (_barcodeBuilder.Length <= 0) return CallNextHookEx(_hookId, nCode, wParam, lParam);
            var barcode = _barcodeBuilder.ToString();
            _barcodeBuilder.Clear();
            BarcodeScanned?.Invoke(this, barcode);
        }
        else
        {
            var key = (Keys)vkCode;
            if (char.IsLetterOrDigit((char)key) || key == Keys.OemMinus) _barcodeBuilder.Append((char)key);
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
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

    #endregion
}
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Serilog;

namespace FuzhouPolicyForce;

/// <summary>
///     全局异常处理器，负责捕获和处理应用程序中的所有未处理异常
///     集成Windows错误报告功能，自动生成崩溃转储文件
/// </summary>
internal static class GlobalExceptionHandler
{
    // Windows Error Reporting P/Invoke declarations
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetErrorMode(uint uMode);

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        int processId,
        IntPtr hFile,
        int dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetCurrentProcessId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateFileW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    // Constants for Windows Error Reporting
    private const uint SemFailcriticalerrors = 0x0001;
    private const uint SemNogpfaulterrorbox = 0x0002;
    private const uint SemNoalignmentfaultexcept = 0x0004;
    private const uint SemNoopenfileerrorbox = 0x8000;

    private const int MinidumpTypeWithFullMemory = 0x00000002;
    
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareWrite = 0x00000002;
    private const uint CreateAlways = 2;
    private const uint FileAttributeNormal = 0x00000080;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    /// <summary>
    ///     初始化全局异常处理器
    /// </summary>
    public static void Initialize()
    {
        try
        {
            // 配置Windows错误模式，禁用系统错误对话框
            SetErrorMode(SemFailcriticalerrors | SemNogpfaulterrorbox | SemNoalignmentfaultexcept | SemNoopenfileerrorbox);
            Log.Information("Windows错误模式已配置");

            // 订阅UI线程异常
            Application.Current.DispatcherUnhandledException += OnDispatcherUnhandledException;

            // 订阅应用程序域异常
            AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;

            // 订阅异步任务异常
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            Log.Information("全局异常处理器初始化完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "初始化全局异常处理器时发生错误");
            // 即使初始化失败，也要确保基本的异常处理可用
            Application.Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
        }
    }

    /// <summary>
    ///     处理UI线程异常
    /// </summary>
    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        HandleException(e.Exception, "UI线程");
        e.Handled = true;
    }

    /// <summary>
    ///     处理应用程序域异常
    /// </summary>
    private static void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        HandleException(exception, "应用程序域");
    }

    /// <summary>
    ///     处理异步任务异常
    /// </summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // 检查是否是网络连接相关的异常，这些不应该导致应用程序崩溃
        if (IsNetworkRelatedException(e.Exception))
        {
            Log.Warning(e.Exception, "检测到网络相关的未观察Task异常，已忽略");
            e.SetObserved();
            return;
        }
        
        HandleException(e.Exception, "异步任务");
        e.SetObserved();
    }

    /// <summary>
    ///     生成崩溃转储文件
    /// </summary>
    /// <param name="eventSource">异常来源</param>
    private static void GenerateCrashDump(string eventSource)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dumpFileName = $"crash_{eventSource}_{timestamp}.dmp";
            var dumpPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dumpFileName);

            var processHandle = GetCurrentProcess();
            var processId = GetCurrentProcessId();

            // 创建转储文件
            var fileHandle = CreateFileW(
                dumpPath,
                GenericWrite,
                FileShareWrite,
                IntPtr.Zero,
                CreateAlways,
                FileAttributeNormal,
                IntPtr.Zero);

            if (fileHandle != InvalidHandleValue)
            {
                // 生成完整内存转储
                var success = MiniDumpWriteDump(
                    processHandle,
                    processId,
                    fileHandle,
                    MinidumpTypeWithFullMemory,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (success)
                {
                    Log.Information("崩溃转储文件已生成: {DumpPath}", dumpPath);
                }
                else
                {
                    Log.Warning("生成崩溃转储文件失败");
                }
            }
            else
            {
                Log.Warning("无法创建转储文件: {DumpPath}", dumpPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "生成崩溃转储文件时发生错误");
        }
    }

    /// <summary>
    ///     检查是否是网络相关的异常
    /// </summary>
    /// <param name="exception">异常对象</param>
    /// <returns>是否是网络相关异常</returns>
    private static bool IsNetworkRelatedException(Exception exception)
    {
        // 递归检查AggregateException中的所有内部异常
        if (exception is AggregateException aggregateEx)
        {
            return aggregateEx.InnerExceptions.All(IsNetworkRelatedException);
        }

        // 检查异常本身
        if (exception is System.Net.Sockets.SocketException socketEx)
        {
            // 网络连接相关的Socket错误不应该导致应用程序崩溃
            return socketEx.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.ConnectionRefused => true, // 连接被拒绝
                System.Net.Sockets.SocketError.TimedOut => true, // 连接超时
                System.Net.Sockets.SocketError.HostUnreachable => true, // 主机不可达
                System.Net.Sockets.SocketError.NetworkUnreachable => true, // 网络不可达
                System.Net.Sockets.SocketError.ConnectionReset => true, // 连接重置
                System.Net.Sockets.SocketError.ConnectionAborted => true, // 连接中止
                System.Net.Sockets.SocketError.Shutdown => true, // 连接关闭
                System.Net.Sockets.SocketError.OperationAborted => true, // 操作被中止 (995)
                _ => false
            };
        }

        // 检查内部异常
        if (exception.InnerException is System.Net.Sockets.SocketException innerSocketEx)
        {
            return innerSocketEx.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.ConnectionRefused => true,
                System.Net.Sockets.SocketError.TimedOut => true,
                System.Net.Sockets.SocketError.HostUnreachable => true,
                System.Net.Sockets.SocketError.NetworkUnreachable => true,
                System.Net.Sockets.SocketError.ConnectionReset => true,
                System.Net.Sockets.SocketError.ConnectionAborted => true,
                System.Net.Sockets.SocketError.Shutdown => true,
                System.Net.Sockets.SocketError.OperationAborted => true, // 操作被中止 (995)
                _ => false
            };
        }

        // 检查其他网络相关异常
        if (exception is System.Net.WebException or
            IOException or
            OperationCanceledException)
        {
            return true;
        }

        // 递归检查内部异常
        if (exception.InnerException != null)
        {
            return IsNetworkRelatedException(exception.InnerException);
        }

        return false;
    }

    /// <summary>
    ///     统一的异常处理方法
    /// </summary>
    /// <param name="exception">异常对象</param>
    /// <param name="eventSource">异常来源</param>
    private static void HandleException(Exception? exception, string eventSource)
    {
        try
        {
            // 检查是否是网络相关异常，如果是则不导致应用程序崩溃
            if (exception != null && IsNetworkRelatedException(exception))
            {
                Log.Warning(exception, "{Source}发生网络相关异常，已忽略", eventSource);
                return;
            }

            // 记录详细异常信息
            Log.Fatal(exception, "{Source}发生未处理的异常", eventSource);

            // 生成崩溃转储文件
            GenerateCrashDump(eventSource);

            // 确保日志写入磁盘
            Log.CloseAndFlush();

            // 显示用户友好的错误消息
            var message = $"程序发生严重错误，即将关闭。\n\n错误类型: {exception?.GetType().Name}\n错误信息: {exception?.Message}\n\n请查看日志文件和崩溃转储文件了解详细信息。";
            
            // 在UI线程中显示消息框
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(message, "程序错误", MessageBoxButton.OK, MessageBoxImage.Error);
            });

            // 优雅关闭应用程序
            Application.Current.Dispatcher.Invoke(Application.Current.Shutdown);
        }
        catch (Exception ex)
        {
            // 如果异常处理本身出错，至少尝试记录
            try
            {
                Log.Fatal(ex, "处理{Source}异常时发生错误", eventSource);
                Log.CloseAndFlush();
            }
            catch
            {
                // 最后的防线：直接写入文件
                try
                {
                    File.AppendAllText("crash.log", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - 异常处理失败: {ex}\n");
                }
                catch
                {
                    // 完全无法处理，只能强制退出
                }
            }

            // 强制关闭应用程序
            Environment.Exit(1);
        }
    }
} 
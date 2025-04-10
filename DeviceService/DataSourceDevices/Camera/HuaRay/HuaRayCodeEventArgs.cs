using System.Runtime.InteropServices;
using System.Drawing;

namespace DeviceService.DataSourceDevices.Camera.HuaRay;

/// <summary>
/// 华睿相机条码事件参数
/// </summary>
public class HuaRayCodeEventArgs : EventArgs, IDisposable
{
    private bool _disposed;

    /// <summary>
    /// 输出结果类型
    /// 0: 仅条码信息
    /// 1: 包含条码、重量、体积等完整信息
    /// </summary>
    public int OutputResult { get; init; }

    /// <summary>
    /// 相机ID
    /// </summary>
    public string CameraId { get; init; } = string.Empty;

    /// <summary>
    /// 条码列表
    /// </summary>
    public List<string?> CodeList { get; init; } = [];

    /// <summary>
    /// 重量值(g)
    /// </summary>
    public int Weight { get; init; }

    /// <summary>
    /// 体积信息
    /// </summary>
    public HuaRayApiStruct.VolumeInfo VolumeInfo { get; set; }

    /// <summary>
    /// 原始图像
    /// </summary>
    public HuaRayApiStruct.ImageInfo OriginalImage { get; set; }

    /// <summary>
    /// 面单图像
    /// </summary>
    public HuaRayApiStruct.ImageInfo WaybillImage { get; set; }

    /// <summary>
    /// 触发时间戳 (Ticks)
    /// </summary>
    public long TriggerTimeTicks { get; set; }

    /// <summary>
    /// 析构函数
    /// </summary>
    ~HuaRayCodeEventArgs()
    {
        Dispose(false);
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    /// <param name="disposing">是否为显式释放</param>
    // ReSharper disable once UnusedParameter.Local
    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        // 释放复制的内存
        if (OriginalImage.IsCopiedMemory && OriginalImage.ImageData != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(OriginalImage.ImageData);
            // 创建新的结构体实例
            var newOriginalImage = OriginalImage; // 创建副本
            newOriginalImage.ImageData = IntPtr.Zero; // 修改副本
            OriginalImage = newOriginalImage; // 整体替换
        }

        if (WaybillImage.IsCopiedMemory && WaybillImage.ImageData != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(WaybillImage.ImageData);
            // 创建新的结构体实例
            var newWaybillImage = WaybillImage; // 创建副本 
            newWaybillImage.ImageData = IntPtr.Zero; // 修改副本
            WaybillImage = newWaybillImage; // 整体替换
        }

        _disposed = true;
    }
}

/// <summary>
/// 华睿相机状态事件参数
/// </summary>
public class CameraStatusArgs : EventArgs
{
    /// <summary>
    /// 相机用户ID
    /// </summary>
    public string CameraUserId { get; init; } = string.Empty;

    /// <summary>
    /// 相机在线状态
    /// </summary>
    public bool IsOnline { get; init; }
}
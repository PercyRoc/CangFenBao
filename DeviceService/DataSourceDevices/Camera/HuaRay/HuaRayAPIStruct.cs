using System.Runtime.InteropServices;

namespace DeviceService.DataSourceDevices.Camera.HuaRay;

/// <summary>
///     华睿相机API结构定义
/// </summary>
public static class HuaRayApiStruct
{
    /// <summary>
    ///     图像类型枚举
    /// </summary>
    public enum EImageType
    {
        /// <summary>
        ///     普通图像(灰度图)
        /// </summary>
        EImageTypeNormal = 0,

        /// <summary>
        ///     JPEG压缩图像
        /// </summary>
        EImageTypeJpeg = 1,

        /// <summary>
        ///     BGR格式图像
        /// </summary>
        EImageTypeBgr = 2
    }

    /// <summary>
    ///     原始图像信息
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ImageInfo
    {
        /// <summary>
        ///     图像数据指针
        /// </summary>
        public IntPtr ImageData;

        /// <summary>
        ///     图像数据大小
        /// </summary>
        public int dataSize;

        /// <summary>
        ///     图像宽度
        /// </summary>
        public int width;

        /// <summary>
        ///     图像高度
        /// </summary>
        public int height;

        /// <summary>
        ///     图像类型
        /// </summary>
        public int type;

        /// <summary>
        ///     标记是否为复制的内存（需要释放）
        /// </summary>
        public bool IsCopiedMemory;
    }

    /// <summary>
    ///     体积信息
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VolumeInfo
    {
        /// <summary>
        ///     长度(mm)
        /// </summary>
        public float length;

        /// <summary>
        ///     宽度(mm)
        /// </summary>
        public float width;

        /// <summary>
        ///     高度(mm)
        /// </summary>
        public float height;

        /// <summary>
        ///     体积(mm³)
        /// </summary>
        public float volume;
    }

    /// <summary>
    ///     相机信息
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CameraInfo
    {
        /// <summary>
        ///     相机设备ID
        /// </summary>
        public string camDevID;

        /// <summary>
        ///     相机型号名称
        /// </summary>
        public string camDevModelName;

        /// <summary>
        ///     相机序列号
        /// </summary>
        public string camDevSerialNumber;

        /// <summary>
        ///     相机厂商
        /// </summary>
        public string camDevVendor;

        /// <summary>
        ///     相机固件版本
        /// </summary>
        public string camDevFirewareVersion;

        /// <summary>
        ///     相机额外信息
        /// </summary>
        public string camDevExtraInfo;
    }

    /// <summary>
    ///     相机状态信息
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CameraStatusInfo
    {
        /// <summary>
        ///     相机键值
        /// </summary>
        public string key;

        /// <summary>
        ///     相机用户ID
        /// </summary>
        public string deviceUserID;

        /// <summary>
        ///     相机在线状态
        /// </summary>
        public bool isOnline;
    }
}
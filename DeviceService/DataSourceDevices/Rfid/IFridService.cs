using System;
using System.Threading.Tasks;
using Common.Models.Settings;

namespace DeviceService.DataSourceDevices.Rfid;

/// <summary>
/// Frid设备服务接口
/// </summary>
public interface IFridService : IDisposable
{
    /// <summary>
    /// 是否已连接
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 是否已启用
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// 连接状态变更事件
    /// </summary>
    event Action<bool> ConnectionChanged;

    /// <summary>
    /// 标签数据接收事件
    /// </summary>
    event Action<FridTagData> TagDataReceived;

    /// <summary>
    /// 初始化Frid设备
    /// </summary>
    /// <param name="settings">Frid设置</param>
    /// <returns>初始化是否成功</returns>
    Task<bool> InitializeAsync(FridSettings settings);

    /// <summary>
    /// 连接设备
    /// </summary>
    /// <returns>连接是否成功</returns>
    Task<bool> ConnectAsync();

    /// <summary>
    /// 断开连接
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// 设置工作参数
    /// </summary>
    /// <param name="power">功率设置</param>
    /// <returns>设置是否成功</returns>
    Task<bool> SetWorkingParamAsync(int power);

    /// <summary>
    /// 开始盘点
    /// </summary>
    /// <returns>操作是否成功</returns>
    Task<bool> StartInventoryAsync();

    /// <summary>
    /// 停止盘点
    /// </summary>
    /// <returns>操作是否成功</returns>
    Task<bool> StopInventoryAsync();

    /// <summary>
    /// 查询设备信息
    /// </summary>
    /// <returns>设备信息</returns>
    Task<FridDeviceInfo?> QueryDeviceInfoAsync();
}

/// <summary>
/// Frid标签数据
/// </summary>
public class FridTagData
{
    /// <summary>
    /// 标签EPC
    /// </summary>
    public string Epc { get; set; } = string.Empty;

    /// <summary>
    /// 天线号
    /// </summary>
    public int AntennaNo { get; set; }

    /// <summary>
    /// 信号强度
    /// </summary>
    public int Rssi { get; set; }

    /// <summary>
    /// 读取时间
    /// </summary>
    public DateTime ReadTime { get; set; }

    /// <summary>
    /// 用户数据
    /// </summary>
    public byte[]? UserData { get; set; }

    /// <summary>
    /// TID数据
    /// </summary>
    public byte[]? TidData { get; set; }
}

/// <summary>
/// Frid设备信息
/// </summary>
public class FridDeviceInfo
{
    /// <summary>
    /// 固件版本
    /// </summary>
    public string FirmwareVersion { get; set; } = string.Empty;

    /// <summary>
    /// 设备类型
    /// </summary>
    public int DeviceType { get; set; }

    /// <summary>
    /// 设备地址
    /// </summary>
    public ushort DeviceAddress { get; set; }
} 
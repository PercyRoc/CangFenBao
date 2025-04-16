using LogisticsBaseCSharp;
using Serilog;
using System.Runtime.InteropServices;

namespace DeviceService.DataSourceDevices.Camera.HuaRay;

/// <summary>
/// 华睿相机SDK包装类
/// </summary>
public sealed class HuaRayWrapper
{
    #region 私有字段

    /// <summary>
    /// 单例实例
    /// </summary>
    private static readonly Lazy<HuaRayWrapper> LazyInstance = new(() => new HuaRayWrapper());
    
    /// <summary>
    /// LogisticsWrapper实例
    /// </summary>
    private readonly LogisticsWrapper _logisticsWrapper;

    #endregion

    #region 公共属性

    /// <summary>
    /// 获取单例实例
    /// </summary>
    public static HuaRayWrapper Instance => LazyInstance.Value;

    #endregion

    #region 事件定义

    /// <summary>
    /// 条码处理事件
    /// </summary>
    public event EventHandler<HuaRayCodeEventArgs>? CodeHandle;
    
    /// <summary>
    /// 相机状态更新事件
    /// </summary>
    public event EventHandler<CameraStatusArgs>? CameraDisconnectEventHandler;

    #endregion

    #region 构造函数

    /// <summary>
    /// 私有构造函数
    /// </summary>
    private HuaRayWrapper() 
    {
        _logisticsWrapper = LogisticsWrapper.Instance;
    }

    #endregion

    #region 事件处理方法

    /// <summary>
    /// 处理条码事件
    /// </summary>
    private void OnLogisticsCodeEvent(object? sender, LogisticsCodeEventArgs e)
    {
        if (CodeHandle == null) return;
        
        try
        {
            var args = new HuaRayCodeEventArgs
            {
                OutputResult = e.OutputResult,
                CameraId = e.CameraID ?? string.Empty,
                CodeList = e.CodeList ?? [],
                Weight = e.Weight
            };
            
            args.VolumeInfo = new HuaRayApiStruct.VolumeInfo
            {
                length = (float)e.VolumeInfo.length,
                width = (float)e.VolumeInfo.width,
                height = (float)e.VolumeInfo.height,
                volume = (float)e.VolumeInfo.volume
            };

            // 从 Bag_TimeInfo.timeUp 设置 TriggerTimeTicks
            args.TriggerTimeTicks = e.Bag_TimeInfo.timeUp;
            
            if (e.OriginalImage.ImageData != IntPtr.Zero)
            {
                try 
                {
                    var safeSize = Math.Min(e.OriginalImage.dataSize, 50_000_000);
                    
                    if (safeSize > 0 && e.OriginalImage is { width: > 0, height: > 0 })
                    {
                        var copyPtr = Marshal.AllocHGlobal(safeSize);
                        try
                        {
                            LogisticsAPI.CopyMemory(copyPtr, e.OriginalImage.ImageData, safeSize);

                            args.OriginalImage = new HuaRayApiStruct.ImageInfo
                            {
                                ImageData = copyPtr,
                                dataSize = safeSize,
                                width = e.OriginalImage.width,
                                height = e.OriginalImage.height,
                                type = e.OriginalImage.type,
                                IsCopiedMemory = true
                            };
                        }
                        catch (Exception copyEx)
                        {
                            Log.Error(copyEx, "在Wrapper中复制原始图像数据时出错 (Type: {ImageType})", 
                                (LogisticsAPIStruct.EImageType)e.OriginalImage.type);
                            if (copyPtr != IntPtr.Zero) Marshal.FreeHGlobal(copyPtr);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理原始图像信息时发生错误");
                }
            }
            
            if (e.WaybillImage.ImageData != IntPtr.Zero)
            {
                try
                {
                    var safeSize = Math.Min(e.WaybillImage.dataSize, 50_000_000);
                    
                    if (safeSize > 0 && e.WaybillImage is { width: > 0, height: > 0 })
                    {
                        var copyPtr = Marshal.AllocHGlobal(safeSize);
                        try
                        {
                            LogisticsAPI.CopyMemory(copyPtr, e.WaybillImage.ImageData, safeSize);

                            args.WaybillImage = new HuaRayApiStruct.ImageInfo
                            {
                                ImageData = copyPtr,
                                dataSize = safeSize,
                                width = e.WaybillImage.width,
                                height = e.WaybillImage.height,
                                type = e.WaybillImage.type,
                                IsCopiedMemory = true
                            };
                        }
                        catch (Exception copyEx)
                        {
                            Log.Error(copyEx, "在Wrapper中复制面单图像数据时出错 (Type: {ImageType})", 
                                (LogisticsAPIStruct.EImageType)e.WaybillImage.type);
                            if (copyPtr != IntPtr.Zero) Marshal.FreeHGlobal(copyPtr);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理面单图像信息时发生错误");
                }
            }

            if (e.CodesInfo is { Length: > 0 })
            {
                args.CodeList.AddRange(e.CodesInfo.Select(code => code?.ToString() ?? string.Empty));
            }
            
            CodeHandle(this, args);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理相机条码事件时发生错误");
        }
    }
    
    /// <summary>
    /// 处理相机状态事件
    /// </summary>
    private void OnCameraStatusEvent(object? sender, LogisticsBaseCSharp.CameraStatusArgs e)
    {
        if (CameraDisconnectEventHandler == null) return;
        
        try
        {
            var args = new CameraStatusArgs
            {
                CameraUserId = e.CameraUserID,
                IsOnline = e.IsOnline
            };
            CameraDisconnectEventHandler(this, args);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理相机状态事件时发生错误");
        }
    }

    #endregion

    #region 公共方法

    /// <summary>
    /// 初始化SDK
    /// </summary>
    /// <param name="configPath">配置文件路径</param>
    /// <returns>初始化结果</returns>
    public int Initialization(string configPath)
    {
        try
        {
            return _logisticsWrapper.Initialization(configPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "华睿相机SDK初始化失败");
            return -1;
        }
    }
    
    /// <summary>
    /// 启动SDK
    /// </summary>
    /// <returns>启动结果</returns>
    public int Start()
    {
        try
        {
            _logisticsWrapper.CodeHandle += OnLogisticsCodeEvent;
            _logisticsWrapper.CameraDisconnectEventHandler += OnCameraStatusEvent;
            
            return _logisticsWrapper.Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动华睿相机SDK失败");
            return -1;
        }
    }
    
    /// <summary>
    /// 停止SDK
    /// </summary>
    /// <returns>操作是否成功</returns>
    public bool StopApp()
    {
        try
        {
            _logisticsWrapper.CodeHandle -= OnLogisticsCodeEvent;
            _logisticsWrapper.CameraDisconnectEventHandler -= OnCameraStatusEvent;
            
            return _logisticsWrapper.StopApp();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止华睿相机SDK失败");
            return false;
        }
    }
    
    /// <summary>
    /// 注册相机断线回调
    /// </summary>
    public void AttachCameraDisconnectCb()
    {
        try
        {
            _logisticsWrapper.AttachCameraDisconnectCB();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "注册相机断线回调失败");
        }
    }
    
    /// <summary>
    /// 取消注册相机断线回调
    /// </summary>
    /// <returns>操作是否成功</returns>
    public bool DetachCameraDisconnectCb()
    {
        try
        {
            return _logisticsWrapper.DetachCameraDisconnectCB();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "取消注册相机断线回调失败");
            return false;
        }
    }
    
    /// <summary>
    /// 获取工作相机信息
    /// </summary>
    /// <returns>相机信息列表</returns>
    public List<HuaRayApiStruct.CameraInfo> GetWorkCameraInfo()
    {
        try
        {
            var camerasInfo = _logisticsWrapper.GetWorkCameraInfo();
            if (camerasInfo == null)
                return [];

            return camerasInfo.Select(ci => new HuaRayApiStruct.CameraInfo
                {
                    camDevID = ci.camDevID,
                    camDevModelName = ci.camDevModelName,
                    camDevSerialNumber = ci.camDevSerialNumber,
                    camDevVendor = ci.camDevVendor,
                    camDevFirewareVersion = ci.camDevFirewareVersion,
                    camDevExtraInfo = ci.camDevExtraInfo
                })
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取工作相机信息失败");
            return [];
        }
    }

    #endregion
} 
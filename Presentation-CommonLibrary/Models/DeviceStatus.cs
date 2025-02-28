using Prism.Mvvm;

namespace Presentation_CommonLibrary.Models;

/// <summary>
///     设备状态模型
/// </summary>
public class DeviceStatus : BindableBase
{
    private string _icon = string.Empty;
    private string _name = string.Empty;

    private string _status = string.Empty;

    private string _statusColor = "#2196F3";

    private string _description = string.Empty;

    /// <summary>
    ///     设备名称
    /// </summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>
    ///     设备状态
    /// </summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>
    ///     设备图标
    /// </summary>
    public string Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }

    /// <summary>
    ///     状态颜色
    /// </summary>
    public string StatusColor
    {
        get => _statusColor;
        set => SetProperty(ref _statusColor, value);
    }

    /// <summary>
    ///     状态描述
    /// </summary>
    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }
}
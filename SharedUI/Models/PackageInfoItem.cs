namespace SharedUI.Models;

public class PackageInfoItem : BindableBase
{
    private string _description = string.Empty;
    private string _icon = string.Empty;
    private readonly string _label = string.Empty;
    private string _statusColor = "#4CAF50";
    private string _unit = string.Empty;
    private string _value = string.Empty;

    public PackageInfoItem(string label, string value, string unit = "", string description = "", string icon = "")
    {
        Label = label;
        Value = value;
        Unit = unit;
        Description = description;
        Icon = icon;
    }

    /// <summary>
    ///     标签
    /// </summary>
    public string Label
    {
        get => _label;
        private init => SetProperty(ref _label, value);
    }

    /// <summary>
    ///     值
    /// </summary>
    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    /// <summary>
    ///     单位
    /// </summary>
    public string Unit
    {
        get => _unit;
        set => SetProperty(ref _unit, value);
    }

    /// <summary>
    ///     描述
    /// </summary>
    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    /// <summary>
    ///     图标
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
}
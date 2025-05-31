namespace Common.Models;

public class StatisticsItem : BindableBase
{
    private string _description = string.Empty;
    private string _icon = string.Empty;
    private readonly string _label = string.Empty;
    private readonly string _unit = string.Empty;
    private string _value = string.Empty;

    public StatisticsItem(string label, string value, string unit = "", string description = "", string icon = "")
    {
        Label = label;
        Value = value;
        Unit = unit;
        Description = description;
        Icon = icon;
    }

    public string Label
    {
        get => _label;
        private init => SetProperty(ref _label, value);
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public string Unit
    {
        get => _unit;
        private init => SetProperty(ref _unit, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public string Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }
}
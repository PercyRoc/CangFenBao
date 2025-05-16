using Common.Services.Settings;

namespace Weight.Models.Settings;

[Configuration("WeightSettings")]
public class WeightSettings
{
    public bool IsEnabled { get; set; } = true;
    public string PortName { get; set; } = "COM1";
    public int BaudRate { get; set; } = 9600;
    public WeightType WeightType { get; set; } = WeightType.Static;
    public int StableWeightSamples { get; set; } = 5;
    public int IntegrationTimeMs { get; set; } = 100;
} 
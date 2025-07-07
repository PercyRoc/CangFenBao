using Common.Services.Settings;

namespace Weight.Models.Settings;

[Configuration("WeightSettings")]
public class WeightSettings : BindableBase
{
    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    private string _portName = "COM1";
    public string PortName
    {
        get => _portName;
        set => SetProperty(ref _portName, value);
    }

    private int _baudRate = 38400;
    public int BaudRate
    {
        get => _baudRate;
        set => SetProperty(ref _baudRate, value);
    }

    private WeightType _weightType = WeightType.Static;
    public WeightType WeightType
    {
        get => _weightType;
        set => SetProperty(ref _weightType, value);
    }

    private int _stableWeightSamples = 5;
    public int StableWeightSamples
    {
        get => _stableWeightSamples;
        set => SetProperty(ref _stableWeightSamples, value);
    }

    private int _integrationTimeMs = 100;
    public int IntegrationTimeMs
    {
        get => _integrationTimeMs;
        set => SetProperty(ref _integrationTimeMs, value);
    }

    private int _stabilityCheckSamples = 5;
    public int StabilityCheckSamples
    {
        get => _stabilityCheckSamples;
        set => SetProperty(ref _stabilityCheckSamples, value);
    }

    private double _stabilityThresholdGrams = 20.0;
    public double StabilityThresholdGrams
    {
        get => _stabilityThresholdGrams;
        set => SetProperty(ref _stabilityThresholdGrams, value);
    }

    private int _stableWeightQueryWindowSeconds = 2;
    public int StableWeightQueryWindowSeconds
    {
        get => _stableWeightQueryWindowSeconds;
        set => SetProperty(ref _stableWeightQueryWindowSeconds, value);
    }
} 
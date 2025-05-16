using Common.Services.Settings;
using Prism.Mvvm;

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

    private int _baudRate = 9600;
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
} 
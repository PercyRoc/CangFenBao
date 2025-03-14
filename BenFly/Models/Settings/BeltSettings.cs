using System.IO.Ports;
using Common.Services.Settings;
using Prism.Mvvm;

namespace BenFly.Models.Settings;

[Configuration("BeltSettings")]
internal class BeltSettings : BindableBase
{
    private int _baudRate = 9600;
    private int _dataBits = 8;
    private Parity _parity = Parity.None;
    private string _portName = string.Empty;
    private StopBits _stopBits = StopBits.One;

    public string PortName
    {
        get => _portName;
        set => SetProperty(ref _portName, value);
    }

    public int BaudRate
    {
        get => _baudRate;
        set => SetProperty(ref _baudRate, value);
    }

    public int DataBits
    {
        get => _dataBits;
        set => SetProperty(ref _dataBits, value);
    }

    public Parity Parity
    {
        get => _parity;
        set => SetProperty(ref _parity, value);
    }

    public StopBits StopBits
    {
        get => _stopBits;
        set => SetProperty(ref _stopBits, value);
    }
}
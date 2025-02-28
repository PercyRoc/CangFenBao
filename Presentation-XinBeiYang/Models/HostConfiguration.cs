using CommonLibrary.Models.Settings;
using Prism.Mvvm;

namespace Presentation_XinBeiYang.Models;

[Configuration("HostConfiguration")]
public class HostConfiguration : BindableBase
{
    private string _ipAddress = "127.0.0.1";
    private int _port = 8080;

    public string IpAddress
    {
        get => _ipAddress;
        set => SetProperty(ref _ipAddress, value);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }
}
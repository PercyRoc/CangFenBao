using Common.Services.Settings;
using Prism.Mvvm;

namespace Modules.Models;

[Configuration("TcpSettings")]
internal class TcpSettings : BindableBase
{
    private string _address = "127.0.0.1";
    private int _port = 8080;

    public string Address
    {
        get => _address;
        set => SetProperty(ref _address, value);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }
}
using Prism.Mvvm;

namespace Camera.Models.Settings;

public class HikvisionSecurityCameraSettings : BindableBase
{
    private string _ipAddress = "192.168.1.100";
    private int _port = 8000;
    private string _username = "admin";
    private string _password = "";

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

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }
} 
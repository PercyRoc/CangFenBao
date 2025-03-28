using Common.Services.Settings;
using Prism.Mvvm;

namespace XinBeiYang.Models;

[Configuration("HostConfiguration")]
public class HostConfiguration : BindableBase
{
    private string _ipAddress = "127.0.0.1";
    private int _port = 8080;
    private int _uploadTimeoutSeconds = 60; // 默认60秒超时

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

    public int UploadTimeoutSeconds
    {
        get => _uploadTimeoutSeconds;
        set => SetProperty(ref _uploadTimeoutSeconds, value);
    }
}
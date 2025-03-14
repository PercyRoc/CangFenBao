using Common.Services.Settings;
using Prism.Mvvm;

namespace SangNeng.Models.Settings;

[Configuration("sangNengSettings")]
public class SangNengSettings : BindableBase
{
    private string _username = "247";
    private string _password = "2025";

    /// <summary>
    /// Username for SangNeng server
    /// </summary>
    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    /// <summary>
    /// Password for SangNeng server
    /// </summary>
    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }
} 
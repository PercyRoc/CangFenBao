using Common.Services.Settings;

namespace Sunnen.Models.Settings;

[Configuration("sangNengSettings")]
public class SangNengSettings : BindableBase
{
    private string _password = "2025";
    private string _username = "247";
    private string _sign = string.Empty;

    /// <summary>
    ///     Username for SangNeng server
    /// </summary>
    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    /// <summary>
    ///     Password for SangNeng server
    /// </summary>
    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    /// <summary>
    ///     Sign for SangNeng server
    /// </summary>
    public string Sign
    {
        get => _sign;
        set => SetProperty(ref _sign, value);
    }
}
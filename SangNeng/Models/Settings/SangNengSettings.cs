using Common.Services.Settings;
using System.ComponentModel.DataAnnotations;

namespace Sunnen.Models.Settings;

[Configuration("SangNengSettings")]
public class SangNengSettings : BindableBase
{
    private string _password = "2025";
    private string _username = "247";
    private string _sign = "K1A";

    /// <summary>
    ///     Username for SangNeng server
    /// </summary>
    [Required(ErrorMessage = "用户名不能为空")]
    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    /// <summary>
    ///     Password for SangNeng server
    /// </summary>
    [Required(ErrorMessage = "密码不能为空")]
    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    /// <summary>
    ///     Sign for SangNeng server
    /// </summary>
    [Required(ErrorMessage = "签名不能为空")]
    public string Sign
    {
        get => _sign;
        set => SetProperty(ref _sign, value);
    }
}
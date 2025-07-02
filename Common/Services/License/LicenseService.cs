using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace Common.Services.License;

/// <summary>
///     授权服务实现
/// </summary>
public class LicenseService : ILicenseService
{
    private const string AesKey = "Q12W3E4R5T6y7u8i0z9x8c7v6b5n4m3a";
    private readonly string _licensePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.dat");

    /// <summary>
    ///     验证授权
    /// </summary>
    public Task<(bool IsValid, string? Message)> ValidateLicenseAsync()
    {
        try
        {
            var licenseData = GetLicenseData();
            return Task.FromResult(ValidateLicenseData(licenseData));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "授权验证失败");
            return Task.FromResult<(bool IsValid, string? Message)>((false, "软件未授权，请联系厂家获取授权。"));
        }
    }

    /// <summary>
    ///     获取授权过期时间
    /// </summary>
    public Task<DateTime> GetExpirationDateAsync()
    {
        try
        {
            var licenseData = GetLicenseData();
            return Task.FromResult(licenseData.ValidDate);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "获取授权过期时间失败");
            return Task.FromResult(DateTime.MinValue);
        }
    }

    private DateLimitConfig GetLicenseData()
    {
        if (!File.Exists(_licensePath))
        {
            Log.Warning("找不到授权文件: {Path}", _licensePath);
            throw new FileNotFoundException("找不到授权文件", _licensePath);
        }

        try
        {
            var encryptedData = File.ReadAllText(_licensePath);
            var decryptedJson = DecryptAes(encryptedData, AesKey);
            var licenseData = JsonSerializer.Deserialize<DateLimitConfig>(decryptedJson)
                              ?? throw new InvalidOperationException("无效的授权数据");

            return licenseData;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取授权文件失败");
            throw;
        }
    }

    private (bool IsValid, string? Message) ValidateLicenseData(DateLimitConfig config)
    {
        if (!config.IsValid)
        {
            Log.Warning("授权已失效");
            return (false, "软件授权已失效，请联系厂家重新授权。");
        }

        switch (config.CheckDate)
        {
            case false:
                Log.Information("永久授权");
                return (true, null); // 永久授权，不检查时间
            case true:
            {
                var now = DateTime.Now;
                var timeSpan = config.ValidDate - now;
                Log.Information("");
                switch (timeSpan.TotalDays)
                {
                    // 检查是否过期
                    case < 0:
                    {
                        Log.Warning("授权已过期 过期时间: {ExpirationDate}", config.ValidDate);

                        // 更新授权状态
                        if (!config.IsValid) return (false, $"软件授权已过期。\n过期时间：{config.ValidDate:yyyy-MM-dd}\n请联系厂家续期。");
                        config.IsValid = false;
                        config.InvalidDate = now;
                        SaveConfig(config);

                        return (false, $"软件授权已过期。\n过期时间：{config.ValidDate:yyyy-MM-dd}\n请联系厂家续期。");
                    }
                    // 检查剩余时间是否小于一周
                    case <= 7:
                    {
                        var daysLeft = Math.Ceiling(timeSpan.TotalDays);
                        var message = $"授权即将过期，剩余 {daysLeft} 天，请尽快联系厂家续期。";
                        Log.Warning(message);
                        return (true, message); // 返回提醒消息但仍然有效
                    }
                }

                break;
            }
        }

        // 到这里就是没有超过日期
        Log.Information("授权验证通过 过期时间: {ExpirationDate}", config.ValidDate);
        return (true, null);
    }

    private void SaveConfig(DateLimitConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config);
            var encrypted = EncryptAes(json, AesKey);
            File.WriteAllText(_licensePath, encrypted);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存授权状态失败");
        }
    }

    private static string DecryptAes(string source, string key)
    {
        using var aes = Aes.Create();
        aes.Key = GetAesKey(key);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var inputBuffers = Convert.FromBase64String(source);
        var results = decryptor.TransformFinalBlock(inputBuffers, 0, inputBuffers.Length);
        return Encoding.UTF8.GetString(results);
    }

    private static string EncryptAes(string source, string key)
    {
        using var aes = Aes.Create();
        aes.Key = GetAesKey(key);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var inputBuffers = Encoding.UTF8.GetBytes(source);
        var results = encryptor.TransformFinalBlock(inputBuffers, 0, inputBuffers.Length);
        return Convert.ToBase64String(results);
    }

    private static byte[] GetAesKey(string key)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key), "Aes密钥不能为空");
        if (key.Length < 32) key = key.PadRight(32, '0');
        if (key.Length > 32) key = key[..32];
        return Encoding.UTF8.GetBytes(key);
    }
}
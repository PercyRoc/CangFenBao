using System.Security.Cryptography;
using System.Text;

namespace Presentation_XiYiGu.Utils;

/// <summary>
///     AES加密工具类
/// </summary>
public static class AesEncryptionUtil
{
    /// <summary>
    ///     AES加密
    /// </summary>
    /// <param name="plainText">明文</param>
    /// <param name="key">密钥</param>
    /// <returns>Base64编码的密文</returns>
    public static string Encrypt(string plainText, string key)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(key);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        return Convert.ToBase64String(cipherBytes);
    }

    /// <summary>
    ///     AES解密
    /// </summary>
    /// <param name="cipherText">Base64编码的密文</param>
    /// <param name="key">密钥</param>
    /// <returns>明文</returns>
    public static string Decrypt(string cipherText, string key)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(key);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var cipherBytes = Convert.FromBase64String(cipherText);
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);

        return Encoding.UTF8.GetString(plainBytes);
    }
}
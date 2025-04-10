using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace ZtCloudWarehous.Utils;

/// <summary>
///     AES加密工具类
/// </summary>
internal static class AesEncryptionUtil
{
    /// <summary>
    ///     AES加密
    /// </summary>
    /// <param name="plainText">明文</param>
    /// <param name="key">密钥</param>
    /// <returns>Base64编码的密文</returns>
    internal static string Encrypt(string plainText, string key)
    {
        try
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
        catch (Exception ex)
        {
            Log.Error(ex, "AES加密时发生错误");
            throw;
        }
    }
} 
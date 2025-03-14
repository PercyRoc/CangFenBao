using System.Security.Cryptography;
using System.Text;

namespace Presentation_XiYiGu.Utils;

/// <summary>
///     MD5工具类
/// </summary>
public static class Md5Util
{
    /// <summary>
    ///     计算MD5哈希值
    /// </summary>
    /// <param name="input">输入字符串</param>
    /// <returns>MD5哈希值</returns>
    public static string ComputeMd5(string input)
    {
        using var md5 = MD5.Create();
        var inputBytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = md5.ComputeHash(inputBytes);

        // 将字节数组转换为小写十六进制字符串
        var sb = new StringBuilder();
        foreach (var b in hashBytes) sb.Append(b.ToString("x2"));

        return sb.ToString();
    }
}
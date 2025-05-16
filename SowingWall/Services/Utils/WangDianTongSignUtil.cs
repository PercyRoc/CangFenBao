using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SowingWall.Services.Utils
{
    public static class WangDianTongSignUtil
    {
        // 用于移除签名字符串中特定空白字符的正则表达式 (步骤 5)
        private static readonly Regex WhitespaceRegex = new Regex("[\t\r\n ]", RegexOptions.Compiled);

        /// <summary>
        /// 根据新的规范生成旺店通 API 签名。
        /// 公共参数排序 + JSON请求体 + AppSecret 包裹 + 去空白 + MD5
        /// </summary>
        /// <param name="publicParameters">包含所有公共参数（除了 sign 本身）的字典</param>
        /// <param name="jsonRequestBody">JSON 格式的请求体字符串</param>
        /// <param name="appSecret">AppSecret</param>
        /// <returns>MD5 签名字符串</returns>
        public static string GenerateSign(Dictionary<string, string> publicParameters, string jsonRequestBody, string appSecret)
        {
            // 步骤 1 & 2: 将公共参数按 key 排序并拼接 key 和 value
            var sortedPublicParams = publicParameters
                .Where(p => !string.IsNullOrEmpty(p.Value)) // 确保过滤空值公共参数
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key}{p.Value}");
            string concatenatedPublicParams = string.Concat(sortedPublicParams);

            // 步骤 3: 在后面拼接请求体 (JSON string)
            string combined = $"{concatenatedPublicParams}{jsonRequestBody}";

            // 步骤 4: 在首尾拼接 appSecret
            string stringWithSecret = $"{appSecret}{combined}{appSecret}";

            // 步骤 5: 去除字符串中的 "\t", "\r", "\n", " " 空白字符
            string cleanedString = WhitespaceRegex.Replace(stringWithSecret, "");

            // 步骤 6: 生成32位 MD5 大写签名值
            using (MD5 md5 = MD5.Create())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(cleanedString);
                byte[] hashBytes = md5.ComputeHash(inputBytes);

                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("X2")); // X2 for uppercase hex
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// 准备请求组件：JSON 请求体 和 包含所有公共参数（含签名）的字典。
        /// </summary>
        /// <typeparam name="T">业务请求参数对象的类型</typeparam>
        /// <param name="businessRequest">业务请求参数对象</param>
        /// <param name="sid">卖家SID</param>
        /// <param name="appKey">AppKey</param>
        /// <param name="appSecret">AppSecret</param>
        /// <param name="method">API方法名</param>
        /// <returns>一个包含 jsonRequestBody 和 publicParameters (含 sign) 的元组</returns>
        public static (string jsonRequestBody, Dictionary<string, string> publicParameters) PrepareRequestComponents<T>(
            T businessRequest,
            string sid,
            string appKey,
            string appSecret,
            string method)
        {
            // 序列化业务请求为 JSON 字符串
            string jsonRequestBody = JsonSerializer.Serialize(businessRequest, 
                new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

            // 准备公共参数字典 (不含 sign)
            var publicParameters = new Dictionary<string, string>
            {
                { "sid", sid },
                { "appkey", appKey },
                { "method", method },
                { "timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                { "format", "json" }, // 响应格式仍为 json
                { "sign_method", "md5" }
            };
            
            // 生成签名 (使用新的签名方法)
            string sign = GenerateSign(publicParameters, jsonRequestBody, appSecret);

            // 将签名添加到公共参数字典
            var finalPublicParameters = new Dictionary<string, string>(publicParameters)
            {
                { "sign", sign }
            };

            return (jsonRequestBody, finalPublicParameters);
        }
    }
} 
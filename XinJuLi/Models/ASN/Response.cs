using System.Text.Json.Serialization;

namespace XinJuLi.Models.ASN
{
    /// <summary>
    /// 统一API响应模型
    /// </summary>
    public class Response<T>
    {
        /// <summary>
        /// 是否成功
        /// </summary>
        [JsonPropertyName("success")]
        public bool? Success { get; set; }

        /// <summary>
        /// 请求结果代码
        /// </summary>
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        /// <summary>
        /// 请求结果描述
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 时间
        /// </summary>
        [JsonPropertyName("time")]
        public string Time { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        /// <summary>
        /// 业务返回数据
        /// </summary>
        [JsonPropertyName("object")]
        public T? Object { get; set; }

        /// <summary>
        /// 创建成功响应
        /// </summary>
        public static Response<T> CreateSuccess(T? data = default)
        {
            return new Response<T>
            {
                Success = true,
                Code = "SUCCESS",
                Message = "操作成功",
                Object = data
            };
        }

        /// <summary>
        /// 创建失败响应
        /// </summary>
        public static Response<T> CreateFailed(string message, string code = "ERROR")
        {
            return new Response<T>
            {
                Success = false,
                Code = code,
                Message = message
            };
        }
    }

    /// <summary>
    /// 无数据的响应
    /// </summary>
    public class Response : Response<object>
    {
        /// <summary>
        /// 创建成功响应
        /// </summary>
        public static Response CreateSuccess()
        {
            return new Response
            {
                Success = true,
                Code = "SUCCESS",
                Message = "操作成功"
            };
        }

        /// <summary>
        /// 创建失败响应
        /// </summary>
        public new static Response CreateFailed(string message, string code = "ERROR")
        {
            return new Response
            {
                Success = false,
                Code = code,
                Message = message
            };
        }
    }
} 
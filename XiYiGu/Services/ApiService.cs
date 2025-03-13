using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Presentation_XiYiGu.Models;
using Presentation_XiYiGu.Utils;
using Serilog;

namespace Presentation_XiYiGu.Services
{
    /// <summary>
    /// API服务，用于与服务器通信
    /// </summary>
    public class ApiService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly string _aesKey;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="httpClient">HTTP客户端</param>
        /// <param name="baseUrl">基础URL</param>
        /// <param name="aesKey">AES密钥</param>
        public ApiService(HttpClient httpClient, string baseUrl, string aesKey)
        {
            _httpClient = httpClient;
            _baseUrl = baseUrl;
            _aesKey = aesKey;
        }

        /// <summary>
        /// 上传运单记录
        /// </summary>
        /// <param name="machineMx">设备编号</param>
        /// <param name="waybills">运单记录列表</param>
        /// <returns>上传结果</returns>
        public async Task<ApiResponse> UploadWaybillAsync(string machineMx, List<WaybillRecord> waybills)
        {
            try
            {
                // 1. 构建请求参数
                var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                var requestData = new WaybillUploadRequest
                {
                    MachineMx = machineMx,
                    Data = waybills,
                    Timestamp = timestamp
                };

                // 2. 序列化请求数据
                var jsonData = JsonSerializer.Serialize(requestData);

                // 3. 生成签名
                var parameters = new Dictionary<string, string>
                {
                    { "machineMx", machineMx },
                    { "data", jsonData },
                    { "timestamp", timestamp.ToString() }
                };
                var signature = SignatureUtil.GenerateMd5Signature(parameters, _aesKey);
                requestData.Signature = signature;

                // 4. 重新序列化带签名的请求数据
                jsonData = JsonSerializer.Serialize(requestData);

                // 5. AES加密
                var encryptedData = AesEncryptionUtil.Encrypt(jsonData, _aesKey);

                // 6. 发送请求
                var url = $"{_baseUrl}/jt/upload_waybill";
                var content = new StringContent(encryptedData, Encoding.UTF8, "application/json");
                
                Log.Information("正在上传运单记录，URL: {Url}, 运单数量: {Count}", url, waybills.Count);
                
                var response = await _httpClient.PostAsync(url, content);
                
                // 7. 处理响应
                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    Log.Information("上传运单记录成功，响应: {Response}", responseContent);
                    
                    var apiResponse = JsonSerializer.Deserialize<ApiResponse>(responseContent);
                    return apiResponse ?? new ApiResponse { Code = 200, Msg = "操作成功" };
                }
                
                Log.Error("上传运单记录失败，状态码: {StatusCode}", response.StatusCode);
                return new ApiResponse
                {
                    Code = (int)response.StatusCode,
                    Msg = $"请求失败: {response.StatusCode}"
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex, "上传运单记录时发生异常");
                return new ApiResponse
                {
                    Code = 500,
                    Msg = $"请求异常: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// 上传运单图片
        /// </summary>
        /// <param name="machineMx">设备编号</param>
        /// <param name="waybillNumber">运单号</param>
        /// <param name="weightTime">称重扫描时间</param>
        /// <param name="imageFilePath">图片文件路径</param>
        /// <returns>上传结果</returns>
        public async Task<ApiResponse> UploadWaybillImageAsync(string machineMx, string waybillNumber, string weightTime, string imageFilePath)
        {
            try
            {
                // 检查文件是否存在
                if (!File.Exists(imageFilePath))
                {
                    Log.Error("上传运单图片失败，文件不存在: {FilePath}", imageFilePath);
                    return new ApiResponse
                    {
                        Code = 400,
                        Msg = $"文件不存在: {imageFilePath}"
                    };
                }

                // 1. 构建请求参数
                var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                var requestData = new WaybillImageUploadRequest
                {
                    MachineMx = machineMx,
                    Data =
                    [
                        new WaybillImageData
                        {
                            WaybillNumber = waybillNumber,
                            WeightTime = weightTime
                        }
                    ],
                    Timestamp = timestamp
                };

                // 2. 序列化请求数据
                var jsonData = JsonSerializer.Serialize(requestData);

                // 3. 生成签名
                var parameters = new Dictionary<string, string>
                {
                    { "machineMx", machineMx },
                    { "data", jsonData },
                    { "timestamp", timestamp.ToString() }
                };
                var signature = SignatureUtil.GenerateMd5Signature(parameters, _aesKey, true);
                requestData.Signature = signature;

                // 4. 重新序列化带签名的请求数据
                jsonData = JsonSerializer.Serialize(requestData);

                // 5. AES加密
                var encryptedData = AesEncryptionUtil.Encrypt(jsonData, _aesKey);

                // 6. 创建multipart/form-data请求
                var url = $"{_baseUrl}/jt/upload_waybill_img";
                
                using var formData = new MultipartFormDataContent();
                
                // 添加加密字符串
                formData.Add(new StringContent(encryptedData), "encryptStr");
                
                // 添加文件
                var fileContent = new StreamContent(File.OpenRead(imageFilePath));
                var fileName = Path.GetFileName(imageFilePath);
                formData.Add(fileContent, "file", fileName);
                
                Log.Information("正在上传运单图片，URL: {Url}, 运单号: {WaybillNumber}", url, waybillNumber);
                
                // 7. 发送请求
                var response = await _httpClient.PostAsync(url, formData);
                
                // 8. 处理响应
                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    Log.Information("上传运单图片成功，响应: {Response}", responseContent);
                    
                    var apiResponse = JsonSerializer.Deserialize<ApiResponse>(responseContent);
                    return apiResponse ?? new ApiResponse { Code = 200, Msg = "操作成功" };
                }
                
                Log.Error("上传运单图片失败，状态码: {StatusCode}", response.StatusCode);
                return new ApiResponse
                {
                    Code = (int)response.StatusCode,
                    Msg = $"请求失败: {response.StatusCode}"
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex, "上传运单图片时发生异常");
                return new ApiResponse
                {
                    Code = 500,
                    Msg = $"请求异常: {ex.Message}"
                };
            }
        }
    }
} 
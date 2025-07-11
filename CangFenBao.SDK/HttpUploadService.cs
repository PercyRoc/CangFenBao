using System.Text.Json;
using System.Windows.Media.Imaging;
using Common.Models.Package;
using Serilog;
using System.Text;

namespace CangFenBao.SDK
{
    /// <summary>
    /// HTTP上传服务，负责将包裹数据上传到指定的API端点。
    /// </summary>
    internal class HttpUploadService
    {
        private static readonly HttpClient HttpClient = new();
        private readonly SdkConfig _sdkConfig;
        // 新增：缓存JsonSerializerOptions
        private static readonly JsonSerializerOptions CamelCaseOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public HttpUploadService(SdkConfig sdkConfig)
        {
            _sdkConfig = sdkConfig;
            Log.Debug("HttpUploadService 初始化，上传URL: {UploadUrl}, 启用状态: {EnableUpload}", 
                _sdkConfig.UploadUrl, _sdkConfig.EnableUpload);
        }

        /// <summary>
        /// 异步上传包裹数据到配置的API端点。
        /// </summary>
        /// <param name="package">要上传的包裹信息</param>
        /// <returns>服务器响应，如果上传失败则返回null</returns>
        public async Task<UploadResponse?> UploadPackageAsync(PackageInfo package)
        {
            if (!_sdkConfig.EnableUpload || string.IsNullOrEmpty(_sdkConfig.UploadUrl))
            {
                Log.Warning("上传功能未启用或上传URL未配置，跳过包裹 {Barcode} 的上传", package.Barcode);
                return null;
            }

            Log.Information("开始上传包裹 {Barcode} 到 {UploadUrl}", package.Barcode, _sdkConfig.UploadUrl);

            try
            {
                var request = new UploadRequest
                {
                    Barcode = package.Barcode,
                    Weight = package.Weight,
                    Length = package.Length ?? 0,
                    Width = package.Width ?? 0,
                    Height = package.Height ?? 0,
                    Volume = package.Volume ?? 0,
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };

                // 如果启用图片上传且包裹有图片，则添加图片数据
                if (_sdkConfig.UploadImage && package.Image != null)
                {
                    Log.Debug("准备上传包裹 {Barcode} 的图片，尺寸: {Width}x{Height}", 
                        package.Barcode, package.Image.PixelWidth, package.Image.PixelHeight);
                    
                    request.Image = ConvertImageToBase64(package.Image);
                    request.ImageName = $"{package.Barcode}_{DateTime.Now:yyyyMMddHHmmss}.jpg";
                    
                    Log.Debug("包裹 {Barcode} 图片转换完成，Base64长度: {Length}", 
                        package.Barcode, request.Image?.Length ?? 0);
                }

                var json = JsonSerializer.Serialize(request, CamelCaseOptions);

                Log.Debug("包裹 {Barcode} 序列化完成，JSON长度: {Length} 字符", package.Barcode, json.Length);

                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                Log.Debug("开始发送HTTP请求，包裹: {Barcode}", package.Barcode);
                var httpResponse = await HttpClient.PostAsync(_sdkConfig.UploadUrl, content);

                if (httpResponse.IsSuccessStatusCode)
                {
                    var responseJson = await httpResponse.Content.ReadAsStringAsync();
                    Log.Debug("收到服务器响应，包裹: {Barcode}, 状态码: {StatusCode}, 响应长度: {Length}", 
                        package.Barcode, httpResponse.StatusCode, responseJson.Length);

                    var response = JsonSerializer.Deserialize<UploadResponse>(responseJson, CamelCaseOptions);

                    if (response != null)
                    {
                        Log.Information("包裹 {Barcode} 上传成功，服务器响应码: {Code}, 消息: {Message}, 分配格口: {Chute}", 
                            package.Barcode, response.Code, response.Message, response.Chute);
                    }
                    else
                    {
                        Log.Warning("包裹 {Barcode} 上传成功但响应反序列化失败，原始响应: {Response}", 
                            package.Barcode, responseJson);
                    }

                    return response;
                }
                else
                {
                    var errorContent = await httpResponse.Content.ReadAsStringAsync();
                    Log.Error("包裹 {Barcode} 上传失败，HTTP状态码: {StatusCode}, 错误内容: {ErrorContent}", 
                        package.Barcode, httpResponse.StatusCode, errorContent);
                    return null;
                }
            }
            catch (HttpRequestException httpEx)
            {
                Log.Error(httpEx, "包裹 {Barcode} 上传时发生HTTP请求异常，目标URL: {UploadUrl}", 
                    package.Barcode, _sdkConfig.UploadUrl);
                return null;
            }
            catch (TaskCanceledException tcEx)
            {
                if (tcEx.CancellationToken.IsCancellationRequested)
                {
                    Log.Warning("包裹 {Barcode} 上传请求被取消", package.Barcode);
                }
                else
                {
                    Log.Error(tcEx, "包裹 {Barcode} 上传请求超时", package.Barcode);
                }
                return null;
            }
            catch (JsonException jsonEx)
            {
                Log.Error(jsonEx, "包裹 {Barcode} 上传时发生JSON序列化/反序列化异常", package.Barcode);
                return null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "包裹 {Barcode} 上传时发生未预期异常", package.Barcode);
                return null;
            }
        }

        private static string ConvertImageToBase64(BitmapSource bitmapSource)
        {
            try
            {
                Log.Debug("开始转换图片为Base64，尺寸: {Width}x{Height}", 
                    bitmapSource.PixelWidth, bitmapSource.PixelHeight);

                using var memoryStream = new MemoryStream();
                var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                encoder.Save(memoryStream);

                var imageBytes = memoryStream.ToArray();
                var base64String = Convert.ToBase64String(imageBytes);
                
                Log.Debug("图片转换完成，原始字节数: {ByteCount}, Base64长度: {Base64Length}", 
                    imageBytes.Length, base64String.Length);
                
                return base64String;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "图片转换为Base64时发生异常，图片尺寸: {Width}x{Height}", 
                    bitmapSource.PixelWidth, bitmapSource.PixelHeight);
                throw;
            }
        }
    }
} 
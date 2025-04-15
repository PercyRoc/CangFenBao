using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Common.Models.Package;
using Common.Services.Settings;
using Serilog;
using ZtCloudWarehous.Models;
using ZtCloudWarehous.Utils;
using ZtCloudWarehous.ViewModels.Settings;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;

namespace ZtCloudWarehous.Services;

/// <summary>
///     运单上传服务
/// </summary>
public class WaybillUploadService : IWaybillUploadService
{
    private readonly object _queueLock = new();
    private readonly ISettingsService _settingsService;
    private readonly Queue<PackageInfo> _uploadQueue = new();
    private readonly HttpClient _httpClient;
    private XiyiguApiSettings _apiSettings = new();
    private bool _isUploading;

    /// <summary>
    ///     构造函数
    /// </summary>
    /// <param name="settingsService">设置服务</param>
    public WaybillUploadService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        _httpClient = new HttpClient();
        
        // 加载API设置
        LoadApiSettings();
    }

    /// <summary>
    ///     加载API设置
    /// </summary>
    private void LoadApiSettings()
    {
        try
        {
            _apiSettings = _settingsService.LoadSettings<XiyiguApiSettings>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载API设置时发生错误");
        }
    }

    /// <summary>
    ///     添加包裹到后台上传队列（不等待完成）
    /// </summary>
    /// <param name="package">包裹信息</param>
    public void EnqueuePackage(PackageInfo package)
    {
        if (string.IsNullOrEmpty(_apiSettings.MachineMx)) return;

        lock (_queueLock)
        {
            _uploadQueue.Enqueue(package);
        }

        // 如果后台队列没有正在上传，则开始后台上传
        if (!_isUploading)
        {
            _ = ProcessUploadQueueAsync(); // 触发后台处理，不等待
        }
    }

    /// <summary>
    ///     处理后台上传队列
    /// </summary>
    private async Task ProcessUploadQueueAsync()
    {
        if (_isUploading) return;

        _isUploading = true;

        try
        {
            while (true)
            {
                PackageInfo? package;
                lock (_queueLock)
                {
                    if (_uploadQueue.Count == 0) break; // 队列为空则退出

                    package = _uploadQueue.Dequeue();
                }

                // 调用内部上传逻辑处理队列中的包裹
                await UploadPackageInternalAsync(package);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理后台上传队列时发生错误");
        }
        finally
        {
            _isUploading = false;
        }
    }

    /// <summary>
    ///     上传指定包裹并等待其完成。
    /// </summary>
    /// <param name="package">要上传的包裹信息。</param>
    /// <returns>一个表示异步上传操作的任务。</returns>
    public Task UploadPackageAndWaitAsync(PackageInfo package)
    {
        // 直接调用内部上传逻辑，并返回 Task 以供等待
        return UploadPackageInternalAsync(package);
    }

    /// <summary>
    ///     内部上传包裹逻辑 (供后台队列和直接等待调用)
    /// </summary>
    /// <param name="package">包裹信息</param>
    private async Task UploadPackageInternalAsync(PackageInfo? package)
    {
        if (package == null)
        {
            Log.Warning("尝试上传 null 包裹信息，已跳过。");
            return;
        }
        if (string.IsNullOrEmpty(_apiSettings.MachineMx))
        {
            Log.Warning("MachineMx 未配置，无法上传包裹: {Barcode}", package.Barcode);
            return;
        }

        try
        {
            // 转换为运单记录
            var waybill = ConvertToWaybillRecord(package);

            // 上传运单记录
            var response = await UploadWaybillAsync(_apiSettings.MachineMx, [waybill]);

            if (response.IsSuccess)
            {
                Log.Information("运单上传成功，运单号: {WaybillNumber}", package.Barcode);

                // 直接使用包裹对象中的图片属性
                if (package.Image != null)
                {
                    // 上传包裹图片
                    await UploadPackageImageAsync(package);
                }
                else
                {
                    Log.Warning("包裹没有图片，仅上传运单信息：{Barcode}", package.Barcode);
                }
            }
            else
            {
                Log.Warning("运单上传失败，运单号: {WaybillNumber}, 错误: {Error}", package.Barcode, response.Msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "上传包裹时发生错误，运单号: {WaybillNumber}", package.Barcode);
            // 即使上传失败，Task 也正常完成，错误已记录
        }
    }

    /// <summary>
    ///     将包裹信息转换为运单记录
    /// </summary>
    /// <param name="package">包裹信息</param>
    /// <returns>运单记录</returns>
    private static WaybillRecord ConvertToWaybillRecord(PackageInfo package)
    {
        // 计算尺寸字符串
        var sizeStr = string.Empty;
        if (package is { Length: not null, Width: not null, Height: not null })
            // 修改为长*宽*高格式，保持厘米单位
        {
            sizeStr = $"{package.Length.Value:F0}*{package.Width.Value:F0}*{package.Height.Value:F0}";
        }

        // 计算体积字符串
        var volumeStr = string.Empty;
        if (package.Volume.HasValue)
        {
            // 将立方厘米转换为立方米，保留6位小数
            volumeStr = package.Volume.Value.ToString(CultureInfo.InvariantCulture);
        }

        return new WaybillRecord
        {
            WaybillNumber = package.Barcode,
            Weight = package.Weight,
            WeightTime = package.CreateTime.ToString("yyyy-MM-dd HH:mm:ss"),
            JtWaybillSize = sizeStr,
            JtWaybillVolume = volumeStr,
            JtHistoryWeight = package.Weight
        };
    }
    
    /// <summary>
    ///     上传运单记录
    /// </summary>
    /// <param name="machineMx">设备编号</param>
    /// <param name="waybills">运单记录列表</param>
    /// <returns>上传结果</returns>
    private async Task<ApiResponse> UploadWaybillAsync(string machineMx, List<WaybillRecord> waybills)
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
            Log.Debug("上传运单原始JSON数据: {Json}", jsonData);

            // 3. 生成签名
            var parameters = new Dictionary<string, string>
            {
                { "machineMx", machineMx },
                { "data", jsonData },
                { "timestamp", timestamp.ToString() }
            };
            
            try
            {
                var signature = SignatureUtil.GenerateMd5Signature(parameters, _apiSettings.AesKey);
                requestData.Signature = signature;
                Log.Debug("上传运单生成的签名: {Signature}", signature);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "生成运单签名时发生错误");
                throw;
            }

            // 4. 重新序列化带签名的请求数据
            jsonData = JsonSerializer.Serialize(requestData);
            Log.Debug("上传运单带签名的JSON数据: {Json}", jsonData);

            // 5. AES加密
            string encryptedData;
            try
            {
                encryptedData = AesEncryptionUtil.Encrypt(jsonData, _apiSettings.AesKey);
                Log.Debug("上传运单AES加密后数据: {EncryptedData}", encryptedData);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "AES加密运单数据时发生错误: {AesKey}", _apiSettings.AesKey);
                throw;
            }

            // 6. 发送请求
            var url = $"{_apiSettings.BaseUrl}/jt/upload_waybill";
            var content = new StringContent(encryptedData, Encoding.UTF8, "application/json");

            Log.Information("正在上传运单记录，URL: {Url}, 运单数量: {Count}, AesKey长度: {AesKeyLength}, MachineMx: {MachineMx}", 
                url, waybills.Count, _apiSettings.AesKey.Length, machineMx);

            var response = await _httpClient.PostAsync(url, content);

            // 7. 处理响应
            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                Log.Information("上传运单记录成功，响应: {Response}", responseContent);

                var apiResponse = JsonSerializer.Deserialize<ApiResponse>(responseContent);
                return apiResponse ?? new ApiResponse { Code = 200, Msg = "操作成功" };
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            Log.Error("上传运单记录失败，状态码: {StatusCode}, 响应内容: {Content}", response.StatusCode, errorContent);
            
            return new ApiResponse
            {
                Code = (int)response.StatusCode,
                Msg = $"请求失败: {response.StatusCode}, 内容: {errorContent}"
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
    ///     上传包裹图片
    /// </summary>
    /// <param name="package">包裹信息</param>
    private async Task UploadPackageImageAsync(PackageInfo package)
    {
        try
        {
            string tempImagePath;

            // 将图片对象保存到临时文件
            try
            {
                // 创建临时文件夹（如果不存在）
                var tempDir = Path.Combine(Path.GetTempPath(), "ZtCloudWarehous", "TempImages");
                Directory.CreateDirectory(tempDir);

                // 创建临时文件路径
                tempImagePath = Path.Combine(tempDir, $"{package.Barcode}_{DateTime.Now:yyyyMMddHHmmss}.jpg");

                // 保存图片 (BitmapSource to JPEG file)
                if (package.Image != null)
                {
                    var encoder = new JpegBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(package.Image));
                    await using (var fileStream = new FileStream(tempImagePath, FileMode.Create))
                    {
                        encoder.Save(fileStream);
                    }
                    Log.Information("已将包裹图片 (BitmapSource) 保存到临时文件: {FilePath}", tempImagePath);
                }
                else
                {
                     Log.Warning("尝试保存图片但 package.Image 为 null: {Barcode}", package.Barcode);
                     return; // Cannot proceed if image is null
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存包裹图片 (BitmapSource) 到临时文件时发生错误");
                return; // Stop processing if image saving fails
            }

            try
            {
                // 上传图片
                var weightTime = package.CreateTime.ToString("yyyy-MM-dd HH:mm:ss");
                var response = await UploadWaybillImageAsync(
                    _apiSettings.MachineMx,
                    package.Barcode,
                    weightTime,
                    tempImagePath);

                if (response.IsSuccess)
                    Log.Information("运单图片上传成功，运单号: {WaybillNumber}", package.Barcode);
                else
                    Log.Warning("运单图片上传失败，运单号: {WaybillNumber}, 错误: {Error}", package.Barcode, response.Msg);
            }
            finally
            {
                // 删除临时文件
                if (!string.IsNullOrEmpty(tempImagePath) && File.Exists(tempImagePath))
                    try
                    {
                        File.Delete(tempImagePath);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "删除临时图片文件时发生错误: {FilePath}", tempImagePath);
                    }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "上传包裹图片时发生错误，运单号: {WaybillNumber}", package.Barcode);
        }
    }

    /// <summary>
    ///     上传运单图片
    /// </summary>
    /// <param name="machineMx">设备编号</param>
    /// <param name="waybillNumber">运单号</param>
    /// <param name="weightTime">称重扫描时间</param>
    /// <param name="imageFilePath">图片文件路径</param>
    /// <returns>上传结果</returns>
    private async Task<ApiResponse> UploadWaybillImageAsync(string machineMx, string waybillNumber, string weightTime,
        string imageFilePath)
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
            Log.Debug("上传运单图片原始JSON数据: {Json}", jsonData);

            // 3. 生成签名
            var parameters = new Dictionary<string, string>
            {
                { "machineMx", machineMx },
                { "data", jsonData },
                { "timestamp", timestamp.ToString() }
            };
            
            try
            {
                var signature = SignatureUtil.GenerateMd5Signature(parameters, _apiSettings.AesKey, true);
                requestData.Signature = signature;
                Log.Debug("上传运单图片生成的签名: {Signature}", signature);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "生成运单图片签名时发生错误");
                throw;
            }

            // 4. 重新序列化带签名的请求数据
            jsonData = JsonSerializer.Serialize(requestData);
            Log.Debug("上传运单图片带签名的JSON数据: {Json}", jsonData);

            // 5. AES加密
            string encryptedData;
            try
            {
                encryptedData = AesEncryptionUtil.Encrypt(jsonData, _apiSettings.AesKey);
                Log.Debug("上传运单图片AES加密后数据: {EncryptedData}", encryptedData);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "AES加密运单图片数据时发生错误: {AesKey}", _apiSettings.AesKey);
                throw;
            }

            // 6. 创建multipart/form-data请求
            var url = $"{_apiSettings.BaseUrl}/jt/upload_waybill_img";

            using var formData = new MultipartFormDataContent();
            // 添加加密字符串
            formData.Add(new StringContent(encryptedData), "encryptStr");

            // 添加文件
            var fileContent = new StreamContent(File.OpenRead(imageFilePath));
            var fileName = Path.GetFileName(imageFilePath);
            formData.Add(fileContent, "file", fileName);

            Log.Information("正在上传运单图片，URL: {Url}, 运单号: {WaybillNumber}, AesKey长度: {AesKeyLength}, MachineMx: {MachineMx}, 文件: {FileName}", 
                url, waybillNumber, _apiSettings.AesKey.Length, machineMx, fileName);

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

            var errorContent = await response.Content.ReadAsStringAsync();
            Log.Error("上传运单图片失败，状态码: {StatusCode}, 响应内容: {Content}", response.StatusCode, errorContent);
            
            return new ApiResponse
            {
                Code = (int)response.StatusCode,
                Msg = $"请求失败: {response.StatusCode}, 内容: {errorContent}"
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

    /// <summary>
    ///     释放资源
    /// </summary>
    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
} 
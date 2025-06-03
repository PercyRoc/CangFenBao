using Common.Services.Settings;
using JinHuaQiHang.Models.Api;
using JinHuaQiHang.Models.Settings;
using Serilog;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace JinHuaQiHang.Services.Implementations
{
    public class YunDaUploadService : IYunDaUploadService
    {
        private readonly ISettingsService _settingsService;
        private readonly HttpClient _httpClient;

        public YunDaUploadService(ISettingsService settingsService)
        {
            _settingsService = settingsService;
            _httpClient = new HttpClient();
        }

        public async Task<YunDaUploadResult> UploadPackageInfoAsync(Common.Models.Package.PackageInfo packageInfo)
        {
            var settings = _settingsService.LoadSettings<YunDaUploadSettings>();

            string uploadUrl = settings.UploadUrl;

            if (string.IsNullOrEmpty(uploadUrl))
            {
                Log.Information("韵达揽收上传地址为空，跳过上传。");
                return new YunDaUploadResult { Code = "-1", Message = "上传地址为空" };
            }

            if (string.IsNullOrEmpty(packageInfo.Barcode))
            {
                Log.Warning("包裹条码为空，无法上传韵达揽收服务。");
                return new YunDaUploadResult { Code = "-1", Message = "包裹条码为空" };
            }
            
            // 根据用户需求，Id 为随机生成的 19 位数字，DocId 为条码 (需要转换为long)
            string id = GenerateRandom19DigitNumber();
            long docId;
            if (!long.TryParse(packageInfo.Barcode, out docId))
            {
                Log.Warning($"包裹条码 '{packageInfo.Barcode}' 无法转换为长整型作为 doc_id。");
                return new YunDaUploadResult { Code = "-1", Message = "包裹条码格式错误" };
            }

            var request = new YunDaUploadRequest
            {
                PartnerId = settings.PartnerId,
                Password = settings.Password,
                Rc4Key = settings.Rc4Key,
                Orders = new Orders
                {
                    GunId = settings.GunId,
                    RequestTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    OrderList = new List<Order>
                    {
                        new Order
                        {
                            Id = id,
                            DocId = docId,
                            ObjWei = packageInfo.Weight,
                            ScanMan = settings.ScanMan,
                            ScanSite = settings.ScanSite,
                            ScanTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                        }
                    }
                }
            };

            try
            {
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("app-key", settings.AppKey);
                _httpClient.DefaultRequestHeaders.Add("req-time", (DateTimeOffset.Now.ToUnixTimeMilliseconds()).ToString());
                
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(request);
                string sign = CalculateMd5Hash(json + "_" + settings.Secret);
                _httpClient.DefaultRequestHeaders.Add("sign", sign);

                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                Log.Information($"正在上传韵达揽收包裹信息：{{Barcode: {packageInfo.Barcode}, Weight: {packageInfo.Weight}}}");
                var response = await _httpClient.PostAsync(uploadUrl, content);
                response.EnsureSuccessStatusCode();

                var responseContent = await response.Content.ReadAsStringAsync();
                var result = Newtonsoft.Json.JsonConvert.DeserializeObject<YunDaUploadResult>(responseContent);

                if (result.Code == "0000") // Success code for Yunda is "0000"
                {
                    Log.Information($"韵达揽收包裹信息上传成功：{{Barcode: {packageInfo.Barcode}, Message: {result.Message}}}");
                }
                else
                {
                    Log.Warning($"韵达揽收包裹信息上传失败：{{Barcode: {packageInfo.Barcode}, Code: {result.Code}, Message: {result.Message}}}");
                }
                return result;
            }
            catch (HttpRequestException ex)
            {
                Log.Error(ex, $"上传韵达揽收包裹信息时发生 HTTP 请求错误：{{Barcode: {packageInfo.Barcode}}}");
                return new YunDaUploadResult { Code = "-1", Message = $"HTTP 请求错误：{ex.Message}" };
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"上传韵达揽收包裹信息时发生未知错误：{{Barcode: {packageInfo.Barcode}}}");
                return new YunDaUploadResult { Code = "-1", Message = $"未知错误：{ex.Message}" };
            }
        }

        private string GenerateRandom19DigitNumber()
        {
            Random random = new Random();
            char[] digits = new char[19];
            for (int i = 0; i < 19; i++)
            {
                digits[i] = (char)('0' + random.Next(0, 10));
            }
            return new string(digits);
        }

        // Helper method to calculate MD5 hash
        private string CalculateMd5Hash(string input)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(input);
                byte[] hashBytes = md5.ComputeHash(inputBytes);

                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }
} 
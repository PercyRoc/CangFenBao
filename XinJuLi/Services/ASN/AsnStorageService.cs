using System.IO;
using System.Text.Json;
using Serilog;
using XinJuLi.Models.ASN;

namespace XinJuLi.Services.ASN
{
    /// <summary>
    /// ASN单存储服务
    /// </summary>
    public class AsnStorageService : IAsnStorageService
    {
        private readonly string _storagePath;
        private readonly JsonSerializerOptions _jsonOptions;

        public AsnStorageService()
        {
            _storagePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "ASN");
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            // 确保存储目录存在
            if (!Directory.Exists(_storagePath))
            {
                Directory.CreateDirectory(_storagePath);
            }
        }

        /// <summary>
        /// 保存ASN单
        /// </summary>
        public void SaveAsnOrder(AsnOrderInfo asnOrder)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(asnOrder.OrderCode))
                {
                    Log.Warning("尝试保存空订单号的ASN单");
                    return;
                }

                var filePath = GetAsnFilePath(asnOrder.OrderCode);
                var json = JsonSerializer.Serialize(asnOrder, _jsonOptions);
                File.WriteAllText(filePath, json);

                Log.Information("ASN单已保存: {OrderCode}", asnOrder.OrderCode);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存ASN单时发生错误: {OrderCode}", asnOrder.OrderCode);
            }
        }

        /// <summary>
        /// 获取所有保存的ASN单
        /// </summary>
        public List<AsnOrderInfo> GetAllAsnOrders()
        {
            try
            {
                var orders = new List<AsnOrderInfo>();
                var files = Directory.GetFiles(_storagePath, "*.json");

                foreach (var file in files)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var order = JsonSerializer.Deserialize<AsnOrderInfo>(json, _jsonOptions);
                        if (order != null)
                        {
                            orders.Add(order);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "读取ASN单文件失败: {FilePath}", file);
                    }
                }

                return orders.OrderByDescending(x => x.OrderCode).ToList();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "获取所有ASN单时发生错误");
                return [];
            }
        }

        /// <summary>
        /// 根据订单号获取ASN单
        /// </summary>
        public AsnOrderInfo? GetAsnOrder(string orderCode)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(orderCode))
                    return null;

                var filePath = GetAsnFilePath(orderCode);
                if (!File.Exists(filePath))
                    return null;

                var json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<AsnOrderInfo>(json, _jsonOptions);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "获取ASN单时发生错误: {OrderCode}", orderCode);
                return null;
            }
        }

        /// <summary>
        /// 删除ASN单
        /// </summary>
        public bool DeleteAsnOrder(string orderCode)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(orderCode))
                    return false;

                var filePath = GetAsnFilePath(orderCode);
                if (!File.Exists(filePath))
                    return false;

                File.Delete(filePath);
                Log.Information("ASN单已删除: {OrderCode}", orderCode);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "删除ASN单时发生错误: {OrderCode}", orderCode);
                return false;
            }
        }

        private string GetAsnFilePath(string orderCode)
        {
            return Path.Combine(_storagePath, $"{orderCode}.json");
        }
    }
} 
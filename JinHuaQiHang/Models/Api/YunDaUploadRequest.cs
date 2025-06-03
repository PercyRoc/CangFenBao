using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace JinHuaQiHang.Models.Api
{
    public class YunDaUploadRequest
    {
        [JsonProperty("partnerid")]
        public string PartnerId { get; set; }

        [JsonProperty("password")]
        public string Password { get; set; }

        [JsonProperty("rc4Key")]
        public string Rc4Key { get; set; }

        [JsonProperty("orders")]
        public Orders Orders { get; set; }
    }

    public class Orders
    {
        [JsonProperty("gun_id")]
        public long GunId { get; set; }

        [JsonProperty("request_time")]
        public string RequestTime { get; set; }

        [JsonProperty("orders")]
        public List<Order> OrderList { get; set; }
    }

    public class Order
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("doc_id")]
        public long DocId { get; set; }

        [JsonProperty("obj_wei")]
        public double ObjWei { get; set; }

        [JsonProperty("scan_man")]
        public string ScanMan { get; set; }

        [JsonProperty("scan_site")]
        public int ScanSite { get; set; }

        [JsonProperty("scan_time")]
        public string ScanTime { get; set; }
    }
} 
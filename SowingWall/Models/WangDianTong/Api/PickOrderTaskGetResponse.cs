using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SowingWall.Models.WangDianTong.Api
{
    public class PickOrderTaskGetResponse : WdtApiResponseBase
    {
        [JsonPropertyName("content")]
        public List<PickOrderContent>? Content { get; set; }
    }

    public class PickOrderContent
    {
        [JsonPropertyName("pick_no")]
        public string PickNo { get; set; } = string.Empty;

        [JsonPropertyName("owner_no")]
        public string OwnerNo { get; set; } = string.Empty;

        [JsonPropertyName("warehouse_no")]
        public string WarehouseNo { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public int Status { get; set; } // 5 已取消, 10 待拣货, 20 拣货中, 30 拣货完成, 35 分货中, 40 分拣完成, 50 完成

        [JsonPropertyName("batch_no")]
        public string BatchNo { get; set; } = string.Empty;

        [JsonPropertyName("pick_type")]
        public int PickType { get; set; } // 1 相同货品, 2 一单一货, 3 一单多货, 4 二次分拣

        [JsonPropertyName("print_remark")]
        public string PrintRemark { get; set; } = string.Empty;

        [JsonPropertyName("created")]
        public DateTime Created { get; set; }

        [JsonPropertyName("stock_goods_detail")]
        public List<StockGoodsDetail>? StockGoodsDetail { get; set; }
    }

    public class StockGoodsDetail
    {
        [JsonPropertyName("stockout_no")]
        public string StockoutNo { get; set; } = string.Empty;

        [JsonPropertyName("src_order_no")]
        public string? SrcOrderNo { get; set; }

        [JsonPropertyName("stockout_status")]
        public int StockoutStatus { get; set; } // 54获取电子面单, 55已审核, 95已发货

        [JsonPropertyName("picklist_seq")]
        public int PicklistSeq { get; set; }

        [JsonPropertyName("logistics_no")]
        public string LogisticsNo { get; set; } = string.Empty;

        [JsonPropertyName("calc_weight")]
        public string CalcWeight { get; set; } = string.Empty;

        [JsonPropertyName("package_name")]
        public string? PackageName { get; set; }

        [JsonPropertyName("package_name_details")]
        public List<PackageNameDetail>? PackageNameDetails { get; set; }

        [JsonPropertyName("goods_detail")]
        public List<GoodsDetail>? GoodsDetail { get; set; }
    }

    public class PackageNameDetail
    {
        [JsonPropertyName("package_name_detail")]
        public string? PackageNameDetailValue { get; set; } // Renamed to avoid conflict with class name

        [JsonPropertyName("package_qty_detail")]
        public string? PackageQtyDetail { get; set; }

        [JsonPropertyName("package_barcode_detail")]
        public string? PackageBarcodeDetail { get; set; }

        [JsonPropertyName("package_spec_detail")]
        public string? PackageSpecDetail { get; set; }
    }

    public class GoodsDetail
    {
        [JsonPropertyName("spec_no")]
        public string SpecNo { get; set; } = string.Empty;

        [JsonPropertyName("goods_name")]
        public string GoodsName { get; set; } = string.Empty;

        [JsonPropertyName("spec_name")]
        public string SpecName { get; set; } = string.Empty;

        [JsonPropertyName("spec_code")]
        public string SpecCode { get; set; } = string.Empty;

        [JsonPropertyName("remark")]
        public string Remark { get; set; } = string.Empty;

        [JsonPropertyName("barcode")]
        public string Barcode { get; set; } = string.Empty;

        [JsonPropertyName("img_url")]
        public string ImgUrl { get; set; } = string.Empty;

        [JsonPropertyName("volume")]
        public string Volume { get; set; } = string.Empty;

        [JsonPropertyName("gross_weight")]
        public decimal GrossWeight { get; set; }

        [JsonPropertyName("all_barcode")]
        public List<BarcodeInfo>? AllBarcode { get; set; }

        [JsonPropertyName("batch_detail")]
        public List<BatchDetailInfo>? BatchDetail { get; set; }
    }

    public class BarcodeInfo
    {
        [JsonPropertyName("barcode")]
        public string Barcode { get; set; } = string.Empty;
    }

    public class BatchDetailInfo
    {
        [JsonPropertyName("batch_num")]
        public decimal BatchNum { get; set; }

        [JsonPropertyName("batch_no")]
        public string BatchNo { get; set; } = string.Empty;

        // Use string? for dates as they might be empty strings in the JSON
        [JsonPropertyName("product_date")]
        public string? ProductDate { get; set; }

        [JsonPropertyName("expire_date")]
        public string? ExpireDate { get; set; }

        [JsonPropertyName("defect")]
        public int Defect { get; set; } // 0 正品, 1 残品

        [JsonPropertyName("position_no")]
        public string PositionNo { get; set; } = string.Empty;
    }
} 
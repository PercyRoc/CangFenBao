namespace Server.JuShuiTan.Models
{
    public class WeightSendRequest
    {
        /// <summary>
        /// 快递单号
        /// </summary>
        public string LId { get; set; }

        /// <summary>
        /// 重量，kg。传0保存0重量，传-1出库单重量为null。
        /// </summary>
        public double Weight { get; set; }

        /// <summary>
        /// 默认值为1
        /// 0:验货后称重
        /// 1:验货后称重并发货
        /// 2:无须验货称重
        /// 3:无须验货称重并发货
        /// 4:发货后称重
        /// 5:自动判断称重并发货
        /// </summary>
        public int? Type { get; set; }

        /// <summary>
        /// 是否是国际运单号：默认为false国内快递
        /// </summary>
        public bool? IsUnLid { get; set; }

        /// <summary>
        /// 体积（单位：立方米）
        /// </summary>
        public double? FVolume { get; set; }

        /// <summary>
        /// 备注称重源，显示在订单操作日志中
        /// </summary>
        public string? Channel { get; set; }
    }
} 
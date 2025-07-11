namespace CangFenBao.SDK
{
    /// <summary>
    /// 用于初始化分拣系统SDK的配置参数。
    /// </summary>
    public class SdkConfig
    {
        /// <summary>
        /// 华睿相机 'LogisticsBase.cfg' 文件的绝对路径。
        /// SDK将不再自动搜索，必须由用户明确提供。
        /// </summary>
        public required string HuaRayConfigPath { get; set; }

        /// <summary>
        /// 串口设置文件的绝对路径 (JSON格式)。
        /// </summary>
        public required string SerialPortSettingsPath { get; set; }

        /// <summary>
        /// 小车硬件参数配置文件的绝对路径 (JSON格式)。
        /// </summary>
        public required string CarSettingsPath { get; set; }

        /// <summary>
        /// 小车分拣序列配置文件的绝对路径 (JSON格式)。
        /// </summary>
        public required string CarSequenceSettingsPath { get; set; }

        /// <summary>
        /// 是否保存相机捕获的图像。默认为 false。
        /// </summary>
        public bool SaveImages { get; set; }

        /// <summary>
        /// 图像保存的目录路径。当 SaveImages 为 true 时此项为必需。
        /// </summary>
        public string? ImageSavePath { get; set; }

        /// <summary>
        /// 是否在保存的图像上添加水印。默认为 false。
        /// </summary>
        public bool AddWatermark { get; set; }

        /// <summary>
        /// 水印内容的格式。
        /// 支持的占位符: {barcode}, {weight}, {size}, {dateTime}。
        /// 默认为 "SN: {barcode} {dateTime}"。
        /// </summary>
        public string WatermarkFormat { get; set; } = "SN: {barcode} {dateTime}";

        /// <summary>
        /// 最小重量阈值（单位：克）。
        /// 如果包裹重量小于此值，将被视为无效包裹并丢弃。
        /// 设置为 0 或负数可禁用此功能。默认为 0。
        /// </summary>
        public double MinimumWeightGrams { get; set; }

        /// <summary>
        /// 是否启用上传包裹数据的功能。默认为 false。
        /// </summary>
        public bool EnableUpload { get; set; }

        /// <summary>
        /// 包裹数据上传的目标URL。当 EnableUpload 为 true 时此项为必需。
        /// </summary>
        public string? UploadUrl { get; set; }

        /// <summary>
        /// 上传数据时是否包含图像的Base64编码。
        /// 这会显著增加请求体的大小。默认为 false。
        /// </summary>
        public bool UploadImage { get; set; }
    }
} 
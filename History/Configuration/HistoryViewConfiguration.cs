namespace History.Configuration
{
    /// <summary>
    /// 提供Package History对话框视图的配置，主要列规范。
    /// </summary>
    public class HistoryViewConfiguration
    {
        /// <summary>
        /// 获取或设置定义要显示/导出的列以及它们应如何显示的列规范列表。
        /// </summary>
        public List<HistoryColumnSpec> ColumnSpecs { get; set; } = [];
    }
} 
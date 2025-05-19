namespace History.Configuration
{
    /// <summary>
    /// 定义历史视图和导出中的列规范。
    /// </summary>
    public class HistoryColumnSpec
    {
        /// <summary>
        /// 获取或设置从PackageHistoryRecord绑定的属性名称。
        /// 例如，"Barcode", "Weight".
        /// </summary>
        public required string PropertyName { get; set; }

        /// <summary>
        /// 获取或设置本地化头文本的资源键。
        /// 例如，"PackageHistory_DataGrid_Header_Barcode".
        /// </summary>
        public required string HeaderResourceKey { get; set; }

        /// <summary>
        /// 获取或设置一个值，指示是否应在DataGrid中显示此列。
        /// </summary>
        public bool IsDisplayed { get; set; } = true;

        /// <summary>
        /// 获取或设置一个值，指示是否应在Excel导出中包含此列。
        /// </summary>
        public bool IsExported { get; set; } = true;

        /// <summary>
        /// 获取或设置此列在DataGrid中的显示顺序（较低的数字优先）。
        /// 在当前迭代中未实现动态XAML列重新排序，但可以用于参考。
        /// </summary>
        public int DisplayOrderInGrid { get; set; }

        /// <summary>
        /// 获取或设置此列在Excel导出中的显示顺序（较低的数字优先）。
        /// </summary>
        public int DisplayOrderInExcel { get; set; }
        
        /// <summary>
        /// 获取或设置DataGrid中列的所需宽度。
        /// 使用null或double.NaN表示自动宽度。如果需要，可以表示为特定约定（例如，-1，然后在VM中处理）。
        /// 为简单起见，这可能直接映射到DataGridColumn.Width（接受DataGridLength）。
        /// 现在使用double?。如果为null，则使用XAML默认值或'Auto'。
        /// </summary>
        public string? Width { get; set; } // Example: "50", "100", "Auto", "*"

        /// <summary>
        /// 获取或设置DataGrid中的绑定字符串格式（例如，"{0:F2}"，"yyyy-MM-dd HH:mm:ss"）。
        /// </summary>
        public string? StringFormat { get; set; }

        /// <summary>
        /// 指示此列是否是模板列（如ViewImage按钮），而不是直接的DataGridTextColumn。
        /// 这有助于ViewModel决定如何处理它，如果列要动态生成。
        /// 对于当前方法（XAML列的可见性），这可能不太重要，但有利于未来。
        /// </summary>
        public bool IsTemplateColumn { get; set; } = false;
    }
} 
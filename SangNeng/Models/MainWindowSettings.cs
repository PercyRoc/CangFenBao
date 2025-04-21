using Common.Services.Settings;

namespace Sunnen.Models;

[Configuration("MainWindowSettings")] // 添加特性，将类关联到加载/保存时使用的键
public class MainWindowSettings
{
    public string? LastSelectedPalletName { get; set; } = "noPallet"; // 默认为空托盘
}
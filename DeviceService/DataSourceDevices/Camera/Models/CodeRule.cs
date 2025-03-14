using System.Text.RegularExpressions;

namespace DeviceService.DataSourceDevices.Camera.Models;

internal class CodeRuleUi
{
    public bool Blength { get; set; }
    public bool Bstartwith { get; set; }
    public bool Bendwith { get; set; }
    public bool Binclude { get; set; }
    public bool Bexclude { get; set; }
    public bool Bother { get; set; }
    public bool Buserdefine { get; set; }
    public int MinLen { get; set; }
    public int MaxLen { get; set; }
    public string Startwith { get; set; } = string.Empty;
    public string Endwith { get; set; } = string.Empty;
    public string Include { get; set; } = string.Empty;
    public int IncludeStart { get; set; }
    public int IncludeEnd { get; set; }
    public string Exclude { get; set; } = string.Empty;
    public int ExcludeStart { get; set; }
    public int ExcludeEnd { get; set; }
    public int Other { get; set; }
}

internal class CodeRule
{
    private Regex? _compiledRegex;
    public string Name { get; set; } = string.Empty;
    internal string Regex { get; set; } = string.Empty;
    public bool UserDefine { get; set; }
    public CodeRuleUi Ui { get; set; } = new();
    internal bool Enable { get; set; }

    internal bool IsMatch(string barcode)
    {
        if (!Enable) return false;

        _compiledRegex ??= new Regex(Regex, RegexOptions.Compiled);
        return _compiledRegex.IsMatch(barcode);
    }
}

public class CodeRules
{
    internal List<CodeRule> Coderules { get; set; } = [];

    internal bool IsValidBarcode(string barcode)
    {
        // 如果没有启用的规则，则认为所有条码都有效
        return !Coderules.Any(static r => r.Enable) ||
               // 只要符合任一启用的规则即为有效
               Coderules.Any(rule => rule.Enable && rule.IsMatch(barcode));
    }
}
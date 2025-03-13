using System.Text.RegularExpressions;

namespace DeviceService.DataSourceDevices.Camera.Models;

public class CodeRuleUI
{
    public bool blength { get; set; }
    public bool bstartwith { get; set; }
    public bool bendwith { get; set; }
    public bool binclude { get; set; }
    public bool bexclude { get; set; }
    public bool bother { get; set; }
    public bool buserdefine { get; set; }
    public int minLen { get; set; }
    public int maxLen { get; set; }
    public string startwith { get; set; } = string.Empty;
    public string endwith { get; set; } = string.Empty;
    public string include { get; set; } = string.Empty;
    public int include_start { get; set; }
    public int include_end { get; set; }
    public string exclude { get; set; } = string.Empty;
    public int exclude_start { get; set; }
    public int exclude_end { get; set; }
    public int other { get; set; }
}

public class CodeRule
{
    public string name { get; set; } = string.Empty;
    public string regex { get; set; } = string.Empty;
    public bool userDefine { get; set; }
    public CodeRuleUI ui { get; set; } = new();
    public bool enable { get; set; }
    
    private Regex? _compiledRegex;
    
    public bool IsMatch(string barcode)
    {
        if (!enable) return false;
        
        _compiledRegex ??= new Regex(regex, RegexOptions.Compiled);
        return _compiledRegex.IsMatch(barcode);
    }
}

public class CodeRules
{
    public List<CodeRule> coderules { get; set; } = new();
    
    public bool IsValidBarcode(string barcode)
    {
        // 如果没有启用的规则，则认为所有条码都有效
        if (!coderules.Any(r => r.enable)) return true;
        
        // 只要符合任一启用的规则即为有效
        return coderules.Any(rule => rule.enable && rule.IsMatch(barcode));
    }
} 
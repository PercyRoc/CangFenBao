using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using Prism.Mvvm;
using Serilog;

namespace CommonLibrary.Models.Settings.Sort;

[Configuration("ChuteConfiguration")]
public class ChuteConfiguration : BindableBase
{
    private int _exceptionChute;
    private ObservableCollection<ChuteRule> _rules = [];

    [RegularExpression(@"^[1-9]\d{0,2}$", ErrorMessage = "异常格口号必须是1-999之间的数字")]
    public int ExceptionChute
    {
        get => _exceptionChute;
        set => SetProperty(ref _exceptionChute, value);
    }

    public ObservableCollection<ChuteRule> Rules
    {
        get => _rules;
        set => SetProperty(ref _rules, value);
    }

    /// <summary>
    /// 根据段码获取格口号
    /// </summary>
    /// <param name="firstSegment">一段码</param>
    /// <param name="secondSegment">二段码</param>
    /// <returns>格口号，如果未找到匹配规则则返回异常格口号</returns>
    private int GetChute(string firstSegment, string secondSegment)
    {
        // 找到所有匹配一段码的规则
        var matchedFirstSegmentRules = Rules.Where(rule =>
            !string.IsNullOrWhiteSpace(rule.FirstSegment) && rule.FirstSegment == firstSegment).ToList();

        // 如果没有匹配一段码的规则，继续下面的匹配逻辑
        if (!matchedFirstSegmentRules.Any())
        {
            // 如果二段码包含横杠，先尝试匹配二级网点段码
            if (!string.IsNullOrWhiteSpace(secondSegment) && secondSegment.Contains('-'))
            {
                var segments = secondSegment.Split('-');
                
                if (segments.Length >= 1)
                {
                    // 先尝试匹配第一部分 (如C01-N01-00中的C01)
                    var matchedRule = Rules.FirstOrDefault(rule =>
                        !string.IsNullOrWhiteSpace(rule.SecondSegment) && rule.SecondSegment == segments[0]);

                    if (matchedRule != null)
                    {
                        return matchedRule.Chute;
                    }

                    // 如果还有更多部分，尝试匹配第二部分
                    if (segments.Length >= 2)
                    {
                        matchedRule = Rules.FirstOrDefault(rule =>
                            !string.IsNullOrWhiteSpace(rule.SecondSegment) && rule.SecondSegment == segments[1]);

                        if (matchedRule != null)
                        {
                            return matchedRule.Chute;
                        }
                    }
                }
            }
            else
            {
                // 如果二段码不包含横杠，直接匹配
                var matchedRule = Rules.FirstOrDefault(rule =>
                    !string.IsNullOrWhiteSpace(rule.SecondSegment) && rule.SecondSegment == secondSegment);

                if (matchedRule != null)
                {
                    return matchedRule.Chute;
                }
            }

            return ExceptionChute;
        }
        
        // 在匹配一段码的规则中查找二段码匹配
        // 如果二段码包含横杠
        if (!string.IsNullOrWhiteSpace(secondSegment) && secondSegment.Contains('-'))
        {
            var segments = secondSegment.Split('-');
            
            if (segments.Length >= 1)
            {
                // 先尝试使用第一部分（如C01）在匹配了一段码的规则中查找
                var perfectMatch = matchedFirstSegmentRules.FirstOrDefault(rule =>
                    !string.IsNullOrWhiteSpace(rule.SecondSegment) && rule.SecondSegment == segments[0]);
                
                if (perfectMatch != null)
                {
                    return perfectMatch.Chute;
                }
                
                // 如果还有更多部分，尝试第二部分
                if (segments.Length >= 2)
                {
                    perfectMatch = matchedFirstSegmentRules.FirstOrDefault(rule =>
                        !string.IsNullOrWhiteSpace(rule.SecondSegment) && rule.SecondSegment == segments[1]);
                    
                    if (perfectMatch != null)
                    {
                        return perfectMatch.Chute;
                    }
                }
            }
        }
        else
        {
            // 如果二段码不包含横杠，在匹配了一段码的规则中查找二段码完全匹配的规则
            var perfectMatch = matchedFirstSegmentRules.FirstOrDefault(rule =>
                !string.IsNullOrWhiteSpace(rule.SecondSegment) && rule.SecondSegment == secondSegment);
            
            if (perfectMatch != null)
            {
                return perfectMatch.Chute;
            }
        }
        
        // 如果有匹配一段码的规则但没有匹配二段码的规则，返回第一个匹配一段码的规则
        return matchedFirstSegmentRules.First().Chute;
    }

    /// <summary>
    /// 根据使用-连接的段码获取格口号
    /// </summary>
    /// <param name="barcode">使用-连接的段码，例如：123-456-789</param>
    /// <returns>格口号，如果未找到匹配规则则返回异常格口号</returns>
    public int GetChuteByConnectedSegments(string barcode)
    {
        // 如果条码为Noread或段码为空，直接返回异常格口
        if (string.IsNullOrWhiteSpace(barcode) || barcode.Equals("Noread", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("条码为Noread或段码为空，使用异常格口");
            return ExceptionChute;
        }

        try
        {
            // 分割段码
            var segments = barcode.Split('-');
            
            // 如果段码数量小于2，返回异常格口
            if (segments.Length < 2)
            {
                return ExceptionChute;
            }

            // 使用前两段进行匹配
            return GetChute(segments[0], segments[1]);
        }
        catch
        {
            return ExceptionChute;
        }
    }

    /// <summary>
    /// 根据空格分隔的段码获取格口号
    /// </summary>
    /// <param name="barcode">使用空格分隔的段码，例如：551 A01-B01 00 或 551 A01 00</param>
    /// <returns>格口号，如果未找到匹配规则则返回异常格口号</returns>
    public int GetChuteBySpaceSeparatedSegments(string barcode)
    {
        // 如果条码为Noread或段码为空，直接返回异常格口
        if (string.IsNullOrWhiteSpace(barcode) || barcode.Equals("Noread", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("条码为Noread或段码为空，使用异常格口");
            return ExceptionChute;
        }

        try
        {
            // 分割段码
            var segments = barcode.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // 如果段码数量小于2，返回异常格口
            return segments.Length < 2 ? ExceptionChute :
                // 使用前两段进行匹配
                GetChute(segments[0], segments[1]);
        }
        catch
        {
            return ExceptionChute;
        }
    }
}

public class ChuteRule : BindableBase, IValidatableObject
{
    private int _chute;
    private string _firstSegment = string.Empty;
    private string _secondSegment = string.Empty;
    private string _thirdSegment = string.Empty;
    private bool _hasError;
    private string _errorMessage = string.Empty;

    public int Chute
    {
        get => _chute;
        set
        {
            if (SetProperty(ref _chute, value))
            {
                ValidateChute();
            }
        }
    }

    public string FirstSegment
    {
        get => _firstSegment;
        set => SetProperty(ref _firstSegment, value);
    }

    public string SecondSegment
    {
        get => _secondSegment;
        set => SetProperty(ref _secondSegment, value);
    }

    public string ThirdSegment
    {
        get => _thirdSegment;
        set => SetProperty(ref _thirdSegment, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    private void ValidateChute()
    {
        if (Chute is < 1 or > 999)
        {
            HasError = true;
            ErrorMessage = "格口号必须是1-999之间的数字";
            return;
        }

        HasError = false;
        ErrorMessage = string.Empty;
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Chute is < 1 or > 999)
        {
            yield return new ValidationResult("格口号必须是1-999之间的数字", [nameof(Chute)]);
        }

        if (string.IsNullOrWhiteSpace(FirstSegment) && 
            string.IsNullOrWhiteSpace(SecondSegment) && 
            string.IsNullOrWhiteSpace(ThirdSegment))
        {
            yield return new ValidationResult("至少需要填写一个段码", [
                nameof(FirstSegment),
                nameof(SecondSegment),
                nameof(ThirdSegment)
            ]);
        }
    }
} 
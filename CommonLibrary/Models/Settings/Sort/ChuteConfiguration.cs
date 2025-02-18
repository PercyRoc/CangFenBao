using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using Prism.Mvvm;

namespace CommonLibrary.Models.Settings.Sort;

[Configuration("ChuteConfiguration")]
public class ChuteConfiguration : BindableBase
{
    private string _exceptionChute = string.Empty;
    private ObservableCollection<ChuteRule> _rules = [];

    [RegularExpression(@"^[1-9]\d{0,2}$", ErrorMessage = "异常格口号必须是1-999之间的数字")]
    public string ExceptionChute
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
    private string GetChute(string firstSegment, string secondSegment)
    {
        // 先匹配一段码
        var matchedRule = Rules.FirstOrDefault(rule =>
            // 如果一段码没有匹配到，则匹配二段码
            !string.IsNullOrWhiteSpace(rule.FirstSegment) && rule.FirstSegment == firstSegment) ?? Rules.FirstOrDefault(rule =>
            !string.IsNullOrWhiteSpace(rule.SecondSegment) && rule.SecondSegment == secondSegment);

        return matchedRule?.Chute ?? ExceptionChute;
    }

    /// <summary>
    /// 根据使用-连接的段码获取格口号
    /// </summary>
    /// <param name="barcode">使用-连接的段码，例如：123-456-789</param>
    /// <returns>格口号，如果未找到匹配规则则返回异常格口号</returns>
    public string GetChuteByConnectedSegments(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
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
}

public class ChuteRule : BindableBase, IValidatableObject
{
    private string _chute = string.Empty;
    private string _firstSegment = string.Empty;
    private string _secondSegment = string.Empty;
    private string _thirdSegment = string.Empty;
    private bool _hasError;
    private string _errorMessage = string.Empty;

    public string Chute
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
        if (string.IsNullOrWhiteSpace(Chute))
        {
            HasError = true;
            ErrorMessage = "格口号不能为空";
            return;
        }

        if (!int.TryParse(Chute, out var chuteNumber) || chuteNumber < 1 || chuteNumber > 999)
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
        if (string.IsNullOrWhiteSpace(Chute))
        {
            yield return new ValidationResult("格口号不能为空", [nameof(Chute)]);
        }
        else if (!int.TryParse(Chute, out var chuteNumber) || chuteNumber < 1 || chuteNumber > 999)
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
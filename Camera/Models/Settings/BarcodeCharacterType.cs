namespace Camera.Models.Settings;

/// <summary>
/// 条码字符类型规则
/// </summary>
public enum BarcodeCharacterType
{
    /// <summary>
    /// 任意字符
    /// </summary>
    Any,
    /// <summary>
    /// 全是数字
    /// </summary>
    AllDigits,
    /// <summary>
    /// 全是字母
    /// </summary>
    AllLetters,
    /// <summary>
    /// 数字和字母
    /// </summary>
    DigitsAndLetters
} 
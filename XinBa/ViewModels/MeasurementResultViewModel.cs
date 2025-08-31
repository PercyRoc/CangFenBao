using System.Text.Json;
using System.Windows.Media.Imaging;
using Common.Models.Package;
using Prism.Mvvm;
using Serilog;
using XinBa.Services;

namespace XinBa.ViewModels;

/// <summary>
///     测量结果的视图模型，包含格式化的测量数据和生成的二维码。
/// </summary>
public class MeasurementResultViewModel : BindableBase
{
    private readonly PackageInfo _packageInfo;
    private readonly IQrCodeService _qrCodeService;

    private string _barcode = string.Empty;
    private string _heightDimText = "0.0 см";
    private string _lengthDimText = "0.0 см";
    private BitmapSource? _qrCodeImage;
    private string _volumeText = "0.0 л";
    private string _weightText = "0.000 кг";
    private string _widthDimText = "0.0 см";

    /// <summary>
    ///     构造函数
    /// </summary>
    /// <param name="packageInfo">包裹信息</param>
    /// <param name="qrCodeService">二维码服务</param>
    public MeasurementResultViewModel(PackageInfo packageInfo, IQrCodeService qrCodeService)
    {
        _packageInfo = packageInfo;
        _qrCodeService = qrCodeService;
        UpdateData();
    }

    /// <summary>
    ///     包裹条码
    /// </summary>
    public string Barcode
    {
        get => _barcode;
        set => SetProperty(ref _barcode, value);
    }

    /// <summary>
    ///     格式化的重量文本
    /// </summary>
    public string WeightText
    {
        get => _weightText;
        set => SetProperty(ref _weightText, value);
    }

    /// <summary>
    ///     格式化的体积文本
    /// </summary>
    public string VolumeText
    {
        get => _volumeText;
        set => SetProperty(ref _volumeText, value);
    }

    /// <summary>
    ///     格式化的宽度文本
    /// </summary>
    public string WidthDimText
    {
        get => _widthDimText;
        set => SetProperty(ref _widthDimText, value);
    }

    /// <summary>
    ///     格式化的高度文本
    /// </summary>
    public string HeightDimText
    {
        get => _heightDimText;
        set => SetProperty(ref _heightDimText, value);
    }

    /// <summary>
    ///     格式化的长度/深度文本
    /// </summary>
    public string LengthDimText
    {
        get => _lengthDimText;
        set => SetProperty(ref _lengthDimText, value);
    }

    /// <summary>
    ///     生成的二维码图像
    /// </summary>
    public BitmapSource? QrCodeImage
    {
        get => _qrCodeImage;
        set => SetProperty(ref _qrCodeImage, value);
    }

    /// <summary>
    ///     根据包裹信息更新所有属性
    /// </summary>
    private void UpdateData()
    {
        try
        {
            // 设置条码
            Barcode = _packageInfo.Barcode;

            // 格式化用于显示的文本
            WeightText = $"{_packageInfo.Weight:F3} кг";
            // 尺寸单位是厘米 (cm)，计算出体积（升）
            var volumeLiters = (_packageInfo.Length ?? 0) * (_packageInfo.Width ?? 0) * (_packageInfo.Height ?? 0) /
                               1000.0;
            VolumeText = $"{volumeLiters:F3} л";

            WidthDimText = $"{_packageInfo.Width ?? 0:F1} см";
            HeightDimText = $"{_packageInfo.Height ?? 0:F1} см";
            LengthDimText = $"{_packageInfo.Length ?? 0:F1} см"; // Assuming Length is Depth

            // 准备要编码到二维码中的数据
            var qrData = new
            {
                barcode = _packageInfo.Barcode,
                weight_kg = _packageInfo.Weight,
                length_cm = _packageInfo.Length,
                width_cm = _packageInfo.Width,
                height_cm = _packageInfo.Height,
                volume_l = volumeLiters
            };

            var jsonString = JsonSerializer.Serialize(qrData);
            QrCodeImage = _qrCodeService.Generate(jsonString);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "为包裹 {Barcode} 更新测量结果数据失败", _packageInfo.Barcode);
        }
    }
}
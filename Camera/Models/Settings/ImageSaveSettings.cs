namespace Camera.Models.Settings;

/// <summary>
/// 图像保存设置
/// </summary>
public class ImageSaveSettings : BindableBase
{
    private string _saveFolderPath = "C:\\CameraImages"; // Default path

    /// <summary>
    /// 图像保存文件夹路径
    /// </summary>
    public string SaveFolderPath
    {
        get => _saveFolderPath;
        set => SetProperty(ref _saveFolderPath, value);
    }
} 
using Prism.Mvvm;
using Presentation_SangNeng.ViewModels.Settings;

namespace Presentation_SangNeng.ViewModels.Windows;

public class SelectableTrayModel : BindableBase
{
    private bool _isSelected;
    private readonly TrayModel _trayModel;
    private string _name = string.Empty;
    private double _weight;
    private double _length;
    private double _width;
    private double _height;

    public SelectableTrayModel(TrayModel trayModel, bool isSelected = false)
    {
        _trayModel = trayModel;
        _isSelected = isSelected;
        _name = trayModel.Name;
        _weight = trayModel.Weight;
        _length = trayModel.Length;
        _width = trayModel.Width;
        _height = trayModel.Height;
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                _trayModel.Name = value;
            }
        }
    }

    public double Weight
    {
        get => _weight;
        set
        {
            if (SetProperty(ref _weight, value))
            {
                _trayModel.Weight = value;
            }
        }
    }

    public double Length
    {
        get => _length;
        set
        {
            if (SetProperty(ref _length, value))
            {
                _trayModel.Length = value;
            }
        }
    }

    public double Width
    {
        get => _width;
        set
        {
            if (SetProperty(ref _width, value))
            {
                _trayModel.Width = value;
            }
        }
    }

    public double Height
    {
        get => _height;
        set
        {
            if (SetProperty(ref _height, value))
            {
                _trayModel.Height = value;
            }
        }
    }

    public TrayModel TrayModel => _trayModel;
} 
using Prism.Mvvm;
using Presentation_SangNeng.ViewModels.Settings;

namespace Presentation_SangNeng.ViewModels.Windows;

public class SelectableTrayModel(TrayModel trayModel, bool isSelected = false) : BindableBase
{
    private bool _isSelected = isSelected;
    private string _name = trayModel.Name;
    private double _weight = trayModel.Weight;
    private double _length = trayModel.Length;
    private double _width = trayModel.Width;
    private double _height = trayModel.Height;

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
                trayModel.Name = value;
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
                trayModel.Weight = value;
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
                trayModel.Length = value;
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
                trayModel.Width = value;
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
                trayModel.Height = value;
            }
        }
    }
} 
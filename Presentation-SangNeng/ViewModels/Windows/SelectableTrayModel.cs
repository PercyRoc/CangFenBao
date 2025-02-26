using Prism.Mvvm;
using Presentation_SangNeng.ViewModels.Settings;

namespace Presentation_SangNeng.ViewModels.Windows;

public class SelectablePalletModel(PalletModel palletModel, bool isSelected = false) : BindableBase
{
    private bool _isSelected = isSelected;
    private string _name = palletModel.Name;
    private double _weight = palletModel.Weight;
    private double _length = palletModel.Length;
    private double _width = palletModel.Width;
    private double _height = palletModel.Height;

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
                palletModel.Name = value;
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
                palletModel.Weight = value;
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
                palletModel.Length = value;
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
                palletModel.Width = value;
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
                palletModel.Height = value;
            }
        }
    }
} 
using Prism.Mvvm;
using Sunnen.ViewModels.Settings;

namespace Sunnen.ViewModels.Windows;

public class SelectablePalletModel(PalletModel palletModel, bool isSelected = false) : BindableBase
{
    private double _height = palletModel.Height;
    private bool _isSelected = isSelected;
    private double _length = palletModel.Length;
    private string _name = palletModel.Name;
    private double _weight = palletModel.Weight;
    private double _width = palletModel.Width;

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
            if (SetProperty(ref _name, value)) palletModel.Name = value;
        }
    }

    public double Weight
    {
        get => _weight;
        set
        {
            if (SetProperty(ref _weight, value)) palletModel.Weight = value;
        }
    }

    public double Length
    {
        get => _length;
        set
        {
            if (SetProperty(ref _length, value)) palletModel.Length = value;
        }
    }

    public double Width
    {
        get => _width;
        set
        {
            if (SetProperty(ref _width, value)) palletModel.Width = value;
        }
    }

    public double Height
    {
        get => _height;
        set
        {
            if (SetProperty(ref _height, value)) palletModel.Height = value;
        }
    }
}
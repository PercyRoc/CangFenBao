using System.Collections.ObjectModel;
using Prism.Mvvm;

namespace CommonLibrary.Models.Settings.Sort;

[Configuration("SortConfiguration")]
public class SortConfiguration : BindableBase
{
    private ObservableCollection<SortPhotoelectric> _sortingPhotoelectrics = [];
    private TriggerPhotoelectric _triggerPhotoelectric = new();

    public TriggerPhotoelectric TriggerPhotoelectric
    {
        get => _triggerPhotoelectric;
        set => SetProperty(ref _triggerPhotoelectric, value);
    }

    public ObservableCollection<SortPhotoelectric> SortingPhotoelectrics
    {
        get => _sortingPhotoelectrics;
        set => SetProperty(ref _sortingPhotoelectrics, value);
    }
}
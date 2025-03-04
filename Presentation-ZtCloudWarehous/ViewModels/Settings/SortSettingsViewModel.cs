using CommonLibrary.Models.Settings.Sort;
using CommonLibrary.Services;
using Prism.Commands;
using Prism.Mvvm;

namespace Presentation_ZtCloudWarehous.ViewModels.Settings;

public class SortSettingsViewModel : BindableBase
{
    private readonly ISettingsService _settingsService;

    private SortConfiguration _configuration = new();

    public SortSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;

        AddPhotoelectricCommand = new DelegateCommand(ExecuteAddPhotoelectric);
        RemovePhotoelectricCommand = new DelegateCommand<SortPhotoelectric>(ExecuteRemovePhotoelectric);
        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);

        // 加载配置
        LoadSettings();
    }

    public SortConfiguration Configuration
    {
        get => _configuration;
        set => SetProperty(ref _configuration, value);
    }

    public DelegateCommand AddPhotoelectricCommand { get; }
    public DelegateCommand<SortPhotoelectric> RemovePhotoelectricCommand { get; }
    public DelegateCommand SaveConfigurationCommand { get; }

    private void ExecuteAddPhotoelectric()
    {
        Configuration.SortingPhotoelectrics.Add(new SortPhotoelectric
        {
            Name = $"光电{Configuration.SortingPhotoelectrics.Count + 1}",
            Port = 2000
        });
    }

    private void ExecuteRemovePhotoelectric(SortPhotoelectric photoelectric)
    {
        Configuration.SortingPhotoelectrics.Remove(photoelectric);
    }

    private void ExecuteSaveConfiguration()
    {
        _settingsService.SaveConfiguration(Configuration);
    }

    private void LoadSettings()
    {
        Configuration = _settingsService.LoadConfiguration<SortConfiguration>();
    }
}
using Common.Services.Settings;
using Prism.Commands;
using Prism.Mvvm;
using SortingServices.Pendulum.Models;

namespace SharedUI.ViewModels.Settings;

public class BalanceSortSettingsViewModel: BindableBase
{
    private readonly ISettingsService _settingsService;

    private PendulumSortConfig _configuration = new();

    public BalanceSortSettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;

        AddPhotoelectricCommand = new DelegateCommand(ExecuteAddPhotoelectric);
        RemovePhotoelectricCommand = new DelegateCommand<SortPhotoelectric>(ExecuteRemovePhotoelectric);
        SaveConfigurationCommand = new DelegateCommand(ExecuteSaveConfiguration);

        // 加载配置
        LoadSettings();
    }

    public PendulumSortConfig Configuration
    {
        get => _configuration;
        private set => SetProperty(ref _configuration, value);
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
        _settingsService.SaveSettings(Configuration);
    }

    private void LoadSettings()
    {
        Configuration = _settingsService.LoadSettings<PendulumSortConfig>();
    }
}
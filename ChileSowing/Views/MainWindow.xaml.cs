using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input; // Required for MouseButtonEventArgs
using ChileSowing.Services;
using ChileSowing.ViewModels;
using Prism.Ioc;
using Serilog;

namespace ChileSowing.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    private readonly ILocalizationService? _localizationService;

    public MainWindow()
    {
        InitializeComponent();
        
        // 获取本地化服务
        _localizationService = ContainerLocator.Container?.Resolve<ILocalizationService>();
        
        // 设置窗口加载时最大化并初始化本地化
        Loaded += (_, _) => {
            WindowState = WindowState.Maximized;
            
            // 初始化MainViewModel的本地化内容
            try
            {
                if (DataContext is MainViewModel mainViewModel)
                {
                    mainViewModel.InitializeLocalization();
                    Log.Information("MainWindow加载完成，MainViewModel本地化内容初始化完成");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "MainWindow加载时初始化MainViewModel本地化内容失败");
            }
        };
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox || comboBox.SelectedItem is not ComboBoxItem selectedItem)
            return;

        var culture = selectedItem.Tag?.ToString();
        if (string.IsNullOrEmpty(culture)) return;

        _localizationService?.ChangeLanguage(culture);
    }
}
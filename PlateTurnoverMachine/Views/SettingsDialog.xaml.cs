﻿using System.Windows;
using SharedUI.Views.Settings;

namespace DongtaiFlippingBoardMachine.Views;

public partial class SettingsDialog
{
    public SettingsDialog()
    {
        InitializeComponent();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RootNavigation?.Navigate(typeof(CameraSettingsView));
    }
}
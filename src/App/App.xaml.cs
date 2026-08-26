using System;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.Activation;
using NvimCore;

namespace NvimWinUIGui;

/// <summary>App entry point: launches the main window (nvim spawn + connect happens there).</summary>
public partial class App : Application
{
    private MainWindow? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_window == null)
            _window = new MainWindow();
        _window.Activate();
    }
}

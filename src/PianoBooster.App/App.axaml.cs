// The Avalonia application of the port (QApplication in QtMain.cpp). GPLv3.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace PianoBooster.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var window = new MainWindow(Program.CommandLine);
            desktop.MainWindow = window;
            window.WindowState = WindowState.Maximized; // showMaximized(): the saved size and position are used when it is restored down
        }
        base.OnFrameworkInitializationCompleted();
    }
}

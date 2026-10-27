using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Pfr.Gui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            // "PFR <backup file>" opens that backup at startup.
            if (desktop.Args is [var path, ..] && File.Exists(path))
            {
                window.Opened += async (_, _) => await window.LoadBackupAsync(path);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}

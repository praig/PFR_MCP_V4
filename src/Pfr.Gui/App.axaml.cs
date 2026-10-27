using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

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

            // Last line of defence: an error in a UI handler is reported, not fatal.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                window.ReportUnexpectedError(e.Exception);
                e.Handled = true;
            };

            // "PFR <backup file>" opens that backup at startup.
            if (desktop.Args is [var path, ..] && File.Exists(path))
            {
                window.Opened += async (_, _) => await window.LoadBackupAsync(path);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}

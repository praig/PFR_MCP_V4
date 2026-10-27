using Avalonia;
using Avalonia.Headless;
using Pfr.Gui.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Pfr.Gui.Tests;

public static class TestAppBuilder
{
    // Real Skia rendering (not headless drawing) so screenshots look like the app.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

namespace Pfr.Gui.Tests;

/// <summary>Main windows backed by a throwaway data folder, so tests never touch real client data.</summary>
internal static class TestWindows
{
    public const int Iterations = 1_000;

    public static string NewDataFolder() => Path.Combine(Path.GetTempPath(), $"pfr-gui-data-{Guid.NewGuid():N}");

    public static MainWindow New(string? dataFolder = null) => new(dataFolder ?? NewDataFolder(), Iterations);
}

namespace Pfr.Gui.Tests;

/// <summary>Main windows backed by a throwaway data folder, so tests never touch real client data.</summary>
internal static class TestWindows
{
    public const int Iterations = 1_000;

    public static string NewDataFolder() => Path.Combine(Path.GetTempPath(), $"pfr-gui-data-{Guid.NewGuid():N}");

    /// <summary>A throwaway folder for report templates, so tests never touch the real one.</summary>
    public static string NewTemplatesFolder() => Path.Combine(Path.GetTempPath(), $"pfr-gui-templates-{Guid.NewGuid():N}");

    public static MainWindow New(string? dataFolder = null, string? templatesFolder = null) =>
        new(dataFolder ?? NewDataFolder(), Iterations, templatesFolder ?? NewTemplatesFolder());
}

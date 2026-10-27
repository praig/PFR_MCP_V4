namespace Pfr.Core.Storage;

/// <summary>
/// Where client data lives. By default a "PfrData" folder next to the application,
/// so the app and its data travel together (e.g. on a USB drive). Override with the
/// PFR_DATA_DIR environment variable or the CLI's --data option.
/// </summary>
public static class DataLocation
{
    public const string FolderName = "PfrData";
    public const string EnvironmentVariable = "PFR_DATA_DIR";

    public static string Resolve(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(overridePath);
        var fromEnv = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return Path.GetFullPath(fromEnv);
        return Path.Combine(AppContext.BaseDirectory, FolderName);
    }

    /// <summary>Checks the folder can be created and written; returns an error message or null.</summary>
    public static string? CheckWritable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Can't write to {folder}: {ex.Message}";
        }
    }
}

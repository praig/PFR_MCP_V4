namespace Pfr.Core.Storage;

/// <summary>
/// Where report templates live: a "ReportTemplates" folder next to the application, so templates travel with it
/// and can be copied to a colleague as plain files. Override with the PFR_TEMPLATES_DIR environment variable or the
/// CLI's --templates option. Unlike client data, templates are not encrypted and need no master password.
/// </summary>
public static class TemplateLocation
{
    public const string FolderName = "ReportTemplates";
    public const string EnvironmentVariable = "PFR_TEMPLATES_DIR";

    public static string Resolve(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(overridePath);
        var fromEnv = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return Path.GetFullPath(fromEnv);
        return Path.Combine(AppContext.BaseDirectory, FolderName);
    }
}

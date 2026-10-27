using System.Reflection;

namespace Pfr.Core;

/// <summary>
/// Basic product information shared by the CLI and GUI.
/// </summary>
public static class AppInfo
{
    public const string ProductName = "PFR_MCP_V4";

    public static string Version =>
        typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+')[0]
        ?? "0.0.0";

    public static string Banner => $"{ProductName} {Version}";
}

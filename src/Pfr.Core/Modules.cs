namespace Pfr.Core;

/// <summary>
/// The functional modules defined in REQUIREMENTS.md. Used to keep
/// the CLI, GUI and docs aligned while the modules are built out.
/// </summary>
public enum AppModule
{
    /// <summary>Client workspace and tracking (REQUIREMENTS §9).</summary>
    ClientWorkspace,

    /// <summary>Inventory and locations (REQUIREMENTS §10).</summary>
    Inventory,

    /// <summary>Ping, SNMP, SSH and DNS tools (REQUIREMENTS §11).</summary>
    NetworkTools,

    /// <summary>Predefined reports (health checks, CNAME resolution…) and a data explorer (REQUIREMENTS §12).</summary>
    Reports,
}

public static class ModuleCatalog
{
    public static IReadOnlyList<(AppModule Module, string Title, string Summary)> All { get; } =
    [
        (AppModule.ClientWorkspace, "Clients",
            "Per-client workspaces, contacts, notes, engagement log and findings."),
        (AppModule.Inventory, "Inventory",
            "Grid members, DNS, IPAM and DHCP objects with site locations."),
        (AppModule.NetworkTools, "Network tools",
            "Ping, SNMP, SSH console and command capture, DNS lookups."),
        (AppModule.Reports, "Reports",
            "Predefined reports (health checks, CNAME resolution, licenses…), a data explorer and remediation change sets."),
    ];
}

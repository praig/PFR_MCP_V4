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

    /// <summary>A client's address plan across all its grids and, later, external sources (REQUIREMENTS §13).</summary>
    Ipam,

    /// <summary>Ping, SNMP, SSH and DNS tools (REQUIREMENTS §11).</summary>
    NetworkTools,

    /// <summary>Predefined reports (health checks, CNAME resolution…) and a data explorer (REQUIREMENTS §12).</summary>
    Reports,

    /// <summary>Placeholder: a future Cloudflare integration. Nothing is planned in detail yet.</summary>
    Cloudflare,

    /// <summary>Placeholder: a future Forward Networks connector (REQUIREMENTS §13.5, §14).</summary>
    ForwardNetworks,

    /// <summary>Placeholder: a future Splunk integration. Nothing is planned in detail yet.</summary>
    Splunk,
}

public static class ModuleCatalog
{
    public static IReadOnlyList<(AppModule Module, string Title, string Summary)> All { get; } =
    [
        (AppModule.ClientWorkspace, "Clients",
            "Per-client workspaces, contacts, notes, engagement log and findings."),
        (AppModule.Inventory, "Inventory",
            "Grid members, DNS, IPAM and DHCP objects with site locations."),
        (AppModule.Ipam, "IPAM",
            "A client's address plan across all its grids: every network once, which grids it is present in, and the DHCP ranges inside."),
        (AppModule.NetworkTools, "Network tools",
            "Ping, DNS lookups (dig), and SSH command capture on appliances. SNMP and an interactive SSH console come later."),
        (AppModule.Reports, "Reports",
            "Predefined reports (health checks, CNAME resolution, licenses…), a data explorer and remediation change sets."),

        // Placeholders: tabs with no page yet, so the places are reserved. Each shows its summary and "Coming in a later version."
        (AppModule.Cloudflare, "Cloudflare",
            "Reserved for a future Cloudflare integration. What it will do has not been decided yet."),
        (AppModule.ForwardNetworks, "Forward Networks",
            "Reserved for a future Forward Networks connector. Until then its networks and devices are meant to come into IPAM through an import."),
        (AppModule.Splunk, "Splunk",
            "Reserved for a future Splunk integration. What it will do has not been decided yet."),
    ];
}

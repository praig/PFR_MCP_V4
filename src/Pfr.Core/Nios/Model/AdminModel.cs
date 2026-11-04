namespace Pfr.Core.Nios.Model;

// Administrator accounts, groups, roles and permissions as stored in a grid backup.
// Only names, flags and relationships are read. Passwords, password hashes, certificates and keys are never read
// (see SensitiveData), so none of this can be used to sign in.

/// <summary>An admin group: the unit permissions and access are granted to.</summary>
public sealed class AdminGroup
{
    public string Name { get; set; } = "";
    public string? Comment { get; set; }

    /// <summary>Superusers can do everything, whatever their permissions say.</summary>
    public bool IsSuperuser { get; set; }
    public bool Disabled { get; set; }

    /// <summary>e.g. FULL_UI.</summary>
    public string? UiModel { get; set; }

    public bool RestrictedUserAccess { get; set; }
}

/// <summary>An administrator account. Each belongs to one group.</summary>
public sealed class AdminUser
{
    public string Name { get; set; } = "";

    /// <summary>The group the user belongs to, or null if the backup doesn't link one.</summary>
    public string? GroupName { get; set; }

    /// <summary>True if the user's group is a superuser group.</summary>
    public bool IsSuperuser { get; set; }

    /// <summary>LOCAL, or REMOTE for users defined on an external authentication server.</summary>
    public string? AuthType { get; set; }

    /// <summary>e.g. ACTIVE.</summary>
    public string? Status { get; set; }
    public bool Disabled { get; set; }

    /// <summary>Accounts NIOS creates for its own use (for example for reporting).</summary>
    public bool IsInternal { get; set; }

    public bool UsesSshKeys { get; set; }
    public DateTimeOffset? LastLogin { get; set; }
    public string? TimeZone { get; set; }
    public string? Comment { get; set; }
}

/// <summary>A role: a named set of permissions that groups can be given.</summary>
public sealed class AdminRole
{
    public string Name { get; set; } = "";
    public string? Comment { get; set; }

    /// <summary>True for the roles NIOS ships with (DNS Admin, DHCP Admin...).</summary>
    public bool IsPredefined { get; set; }
    public bool Disabled { get; set; }
}

/// <summary>A role given to a group.</summary>
public sealed class AdminGroupRole
{
    public string GroupName { get; set; } = "";
    public string RoleName { get; set; } = "";
    public int Position { get; set; }
}

/// <summary>
/// A permission: what an admin group (or a role) may do with a resource, such as a zone or a network.
/// </summary>
public sealed class AdminPermission
{
    public const string GroupOwner = "group";
    public const string RoleOwner = "role";

    /// <summary>"group" or "role".</summary>
    public string OwnerType { get; set; } = "";
    public string OwnerName { get; set; } = "";

    /// <summary>The kind of resource, e.g. dns.zone, dns.network_container, one.cluster.</summary>
    public string ResourceType { get; set; } = "";

    /// <summary>The resource's key: a zone key (._default.example.com), a network key (10.0.0.0/8/0)...</summary>
    public string ResourceKey { get; set; } = "";

    /// <summary>Limits the permission to one kind of object under the resource, e.g. dns.host_address;is_ipv4=true.</summary>
    public string? SubType { get; set; }

    public bool CanRead { get; set; }
    public bool CanModify { get; set; }
    public bool CanDelete { get; set; }
    public bool CanCreate { get; set; }

    /// <summary>True for rules NIOS flags as unmanaged, which it adds itself rather than an administrator.</summary>
    public bool Unmanaged { get; set; }

    /// <summary>Read/Write, Read-only, Deny, or Custom for any other mix.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Level =>
        CanRead && CanModify && CanDelete && CanCreate ? "Read/Write"
        : CanRead && !CanModify && !CanDelete && !CanCreate ? "Read-only"
        : !CanRead && !CanModify && !CanDelete && !CanCreate ? "Deny"
        : "Custom";
}

/// <summary>Whether a group may use an interface: the GUI, the CLI, the API...</summary>
public sealed class AdminAccess
{
    public string OwnerType { get; set; } = "";
    public string OwnerName { get; set; } = "";

    /// <summary>gui, cli, papi, cloud_api or taxii.</summary>
    public string Interface { get; set; } = "";
    public bool Allowed { get; set; }
}

/// <summary>One step in the order the grid tries to authenticate an administrator (Local, then TACACS+, ...).</summary>
public sealed class AdminAuthMethod
{
    public int Position { get; set; }

    /// <summary>e.g. Local, TACACS+, RADIUS, Active Directory, SAML.</summary>
    public string Type { get; set; } = "";
    public string? Name { get; set; }
}
